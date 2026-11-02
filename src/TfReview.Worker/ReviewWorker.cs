using TfReview.Core;
using TfReview.GitHub;
using TfReview.Service;

namespace TfReview.Worker;

/// <summary>Runs one review from a redacted plan and a config. Production's is <c>ReviewRunner</c>.</summary>
public delegate Task<ReviewResult> ReviewEngine(string plan, string? config, string commit, CancellationToken cancellationToken);

/// <summary>Opens a repo through the GitHub App, with a token for that repo alone.</summary>
public delegate Task<RepositorySession> RepositoryOpener(string repository, CancellationToken cancellationToken);

/// <summary>
/// Takes one review off the queue and answers on the PR. Each execution handles one message and exits, so nothing
/// runs between PRs. A failure leaves the message to come back, and the last try says "not reviewed".
/// </summary>
public sealed class ReviewWorker(
    IReviewQueue queue,
    IPlanStore plans,
    IReviewStatusStore statuses,
    RepositoryOpener open,
    ReviewEngine engine,
    Action<string> log,
    TimeProvider? time = null)
{
    /// <summary>Longer than a job may run, so a message only comes back when the job really has gone.</summary>
    public static readonly TimeSpan Visibility = TimeSpan.FromMinutes(20);

    public const int MaxAttempts = 3;

    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>The exit code: 0 once the message is dealt with, and 1 for one that's coming back or given up on.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (await queue.ReceiveAsync(Visibility, cancellationToken) is not { } message)
        {
            log("The queue is empty.");
            return 0;
        }

        if (message.Job is not { } job)
        {
            log("Dropped a message that couldn't be read.");
            await message.CompleteAsync(cancellationToken);
            return 0;
        }

        // A job that keeps dying or running out of time never reaches the catch below, and each try costs model calls.
        if (message.DequeueCount > MaxAttempts)
        {
            log($"Review {job.ReviewId} has used up its tries.");
            await this.GiveUpAsync(job, message);
            return 1;
        }

        try
        {
            if (await this.AlreadyHandledAsync(job, message.DequeueCount, cancellationToken))
            {
                log($"Review {job.ReviewId} was already handled.");
                await message.CompleteAsync(cancellationToken);
                return 0;
            }

            var finished = await this.ReviewAsync(job, cancellationToken);
            await statuses.WriteAsync(finished, cancellationToken);
            await message.CompleteAsync(cancellationToken);
            log($"Review {job.ReviewId}: {finished.State}, {finished.Verdict ?? "no verdict"}.");
            return 0;
        }
        catch (Exception e)
        {
            // Only the type, because a message could quote the plan or a model's answer.
            log($"Review {job.ReviewId} failed on try {message.DequeueCount}: {e.GetType().Name}.");
            if (message.DequeueCount >= MaxAttempts)
            {
                await this.GiveUpAsync(job, message);
            }

            return 1;
        }
    }

    // A second message for one review can come from a retried submit, and it's on its first delivery. A message that
    // has been delivered before is this review's own, coming back after a job failed, so it runs again.
    private async Task<bool> AlreadyHandledAsync(ReviewJob job, long deliveries, CancellationToken cancellationToken)
    {
        var status = await statuses.ReadAsync(job.RepositoryId, job.ReviewId, cancellationToken);
        return status?.State switch
        {
            ReviewState.Done or ReviewState.Failed or ReviewState.Superseded => true,
            ReviewState.Running => deliveries == 1 && this.time.GetUtcNow() - status.UpdatedAt < Visibility,
            _ => false,
        };
    }

    private async Task<ReviewStatus> ReviewAsync(ReviewJob job, CancellationToken cancellationToken)
    {
        await using var session = await open(job.Repository, cancellationToken);

        // A newer push has its own review coming, and an older result mustn't overwrite that.
        var pullRequest = await session.ReadPullRequestAsync(job.PullRequest, cancellationToken);
        if (pullRequest.Head != job.Commit)
        {
            return this.Status(job, ReviewState.Superseded);
        }

        await statuses.WriteAsync(this.Status(job, ReviewState.Running), cancellationToken);

        // At the base commit, so a PR can't weaken its own review.
        var config = await session.ReadFileAsync(job.ConfigPath, pullRequest.Base, cancellationToken);
        var plan = await plans.ReadAsync(job.ReviewId, cancellationToken);
        var result = plan is null
            ? ReviewResult.Failed(job.Commit, ReviewScope.Plan, NotReviewedReason.Unavailable)
            : await engine(plan, config, job.Commit, cancellationToken);

        if ((await session.ReadPullRequestAsync(job.PullRequest, cancellationToken)).Head != job.Commit)
        {
            return this.Status(job, ReviewState.Superseded);
        }

        await PostAsync(session, job, result, Counts(plan), cancellationToken);
        return this.Status(job, ReviewState.Done) with { Verdict = VerdictName(result) };
    }

    // Its own time, because it may be here since the job's deadline passed. Each step can fail by itself.
    private async Task GiveUpAsync(ReviewJob job, ReceivedJob message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var cancellationToken = timeout.Token;
        try
        {
            await using var session = await open(job.Repository, cancellationToken);
            await PostAsync(session, job, ReviewResult.Failed(job.Commit, ReviewScope.Plan, NotReviewedReason.Unavailable), null, cancellationToken);
        }
        catch (Exception e)
        {
            log($"Couldn't post that review {job.ReviewId} failed: {e.GetType().Name}.");
        }

        try
        {
            await statuses.WriteAsync(this.Status(job, ReviewState.Failed), cancellationToken);
        }
        catch (Exception e)
        {
            log($"Couldn't record that review {job.ReviewId} failed: {e.GetType().Name}.");
        }

        await message.CompleteAsync(cancellationToken);
    }

    private static async Task PostAsync(
        RepositorySession session, ReviewJob job, ReviewResult result, PlanCounts? counts, CancellationToken cancellationToken)
    {
        await session.UpsertCommentAsync(job.PullRequest, CommentRenderer.Render(result, counts), cancellationToken);
        await session.PostCheckRunAsync(result, cancellationToken);
    }

    private static PlanCounts? Counts(string? plan) =>
        plan is not null && new PlanRedactor().Redact(plan) is Redacted redacted ? PlanCounts.From(redacted.Plan) : null;

    private static string VerdictName(ReviewResult result) => result.Verdict switch
    {
        Verdict.NoFindings => "no_findings",
        Verdict.Findings => "findings",
        _ => "not_reviewed",
    };

    private ReviewStatus Status(ReviewJob job, ReviewState state) =>
        new(job.ReviewId, job.RepositoryId, job.Repository, job.PullRequest, job.Commit, state, this.time.GetUtcNow());
}
