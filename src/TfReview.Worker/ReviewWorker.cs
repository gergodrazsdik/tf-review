using System.Text.Json;
using TfReview.Core;
using TfReview.GitHub;
using TfReview.Service;

namespace TfReview.Worker;

/// <summary>
/// Runs one review from a redacted plan, a config and the project's lock file, all from the base commit except the plan.
/// Production's is <c>ReviewRunner</c>.
/// </summary>
public delegate Task<ReviewResult> ReviewEngine(
    string plan, string? config, string? lockFile, string commit, string repositoryId, CancellationToken cancellationToken);

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
    TimeProvider? time = null,
    IModelBudget? budget = null,
    IResultStore? results = null)
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
            await this.GiveUpAsync(job, message, NotReviewedReason.Unavailable, "tries_used_up");
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
            // Only the type, because a message could quote the plan or a model's answer. This also catches the job's own
            // setup, such as reading the App's key, which happens after the message is taken, so that it counts as a try
            // and ends in "not reviewed" like any other failure (D39).
            log($"Review {job.ReviewId} failed on try {message.DequeueCount}: {e.GetType().Name}.");
            if (message.DequeueCount >= MaxAttempts)
            {
                var reason = e is OperationCanceledException ? NotReviewedReason.Timeout : NotReviewedReason.Unavailable;
                await this.GiveUpAsync(job, message, reason, e.GetType().Name);
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

        // Also at the base commit: it says which provider docs apply, and a PR's own version isn't merged yet.
        var lockFile = job.WorkingDirectory is { } directory
            ? await session.ReadFileAsync($"{directory.TrimEnd('/')}/.terraform.lock.hcl", pullRequest.Base, cancellationToken)
            : null;
        var plan = await plans.ReadAsync(job.ReviewId, cancellationToken);
        ReviewResult result;
        if (plan is null)
        {
            result = ReviewResult.Failed(job.Commit, ReviewScope.Plan, NotReviewedReason.Unavailable);
        }
        else if (await this.BudgetReachedAsync(cancellationToken))
        {
            // The day's model spend is used up (D62), so nothing is reviewed, and the PR is told why.
            log($"Review {job.ReviewId}: the daily model budget is reached.");
            result = ReviewResult.Failed(job.Commit, ReviewScope.Plan, NotReviewedReason.BudgetReached);
        }
        else
        {
            try
            {
                result = await engine(plan, config, lockFile, job.Commit, job.RepositoryId, cancellationToken);
            }
            finally
            {
                // Whatever happened, what the calls cost counts.
                await this.RecordSpendAsync(job);
            }
        }

        await this.SaveResultAsync(job, result, cancellationToken);

        if ((await session.ReadPullRequestAsync(job.PullRequest, cancellationToken)).Head != job.Commit)
        {
            return this.Status(job, ReviewState.Superseded);
        }

        await PostAsync(session, job, result, Counts(plan), cancellationToken);

        // After posting, so a post that fails and comes back as a retry isn't counted twice.
        ReviewMetrics.RecordResult(result, job.Repository);
        return this.Status(job, ReviewState.Done) with { Verdict = VerdictName(result) };
    }

    // Its own time, because it may be here since the job's deadline passed. Each step can fail by itself. The cause is a
    // fixed word or an exception's type name, for the log and the metric.
    private async Task GiveUpAsync(ReviewJob job, ReceivedJob message, NotReviewedReason reason, string cause)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var cancellationToken = timeout.Token;
        var state = ReviewState.Failed;
        var posted = false;
        try
        {
            await using var session = await open(job.Repository, cancellationToken);

            // Like a review, a give-up for an older commit mustn't replace what a newer push posted.
            if ((await session.ReadPullRequestAsync(job.PullRequest, cancellationToken)).Head != job.Commit)
            {
                state = ReviewState.Superseded;
            }
            else
            {
                await PostAsync(session, job, ReviewResult.Failed(job.Commit, ReviewScope.Plan, reason), null, cancellationToken);
                posted = true;
            }
        }
        catch (Exception e)
        {
            // Nothing reaches the PR, which then shows nothing for this commit, so this has to stand out.
            log($"ERROR: review {job.ReviewId} gave up, and couldn't say so on the PR: {e.GetType().Name}.");
        }

        if (state == ReviewState.Failed)
        {
            ReviewMetrics.RecordGiveUp(job.Repository, cause, posted);
        }

        try
        {
            await statuses.WriteAsync(this.Status(job, state), cancellationToken);
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
        // Ticks on the comment being replaced all carry over, and new ones are marked as counted in it (D56). They're
        // counted once it's written, so a write that fails and is retried can't count them twice.
        IReadOnlyList<string?> counted = [];
        await session.UpsertCommentAsync(
            job.PullRequest,
            previous =>
            {
                var comment = CommentRenderer.Rewrite(result, counts, previous, record: true);
                counted = comment.CountedDismissals;
                return comment.Body;
            },
            cancellationToken);

        foreach (var rule in counted)
        {
            ReviewMetrics.RecordDismissal(rule, job.Repository);
        }

        await session.PostCheckRunAsync(result, cancellationToken);
    }

    // Kept for looking into a bad review later (OPS-19), and never at the cost of posting it.
    private async Task SaveResultAsync(ReviewJob job, ReviewResult result, CancellationToken cancellationToken)
    {
        if (results is null)
        {
            return;
        }

        try
        {
            await results.SaveAsync(job.RepositoryId, job.ReviewId, JsonSerializer.Serialize(result), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Couldn't keep review {job.ReviewId}'s result: {e.GetType().Name}.");
        }
    }

    // A budget store that fails doesn't stop reviews: the Azure budget's alerts are still there behind it.
    private async Task<bool> BudgetReachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return budget is not null && await budget.ReachedAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Couldn't read the daily model budget, so the review goes ahead: {e.GetType().Name}.");
            return false;
        }
    }

    // Its own time, because the review's may have run out.
    private async Task RecordSpendAsync(ReviewJob job)
    {
        if (budget is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await budget.RecordAsync(timeout.Token);
        }
        catch (Exception e)
        {
            log($"Couldn't record review {job.ReviewId}'s model spend: {e.GetType().Name}.");
        }
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
