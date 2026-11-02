using System.Text.RegularExpressions;

namespace TfReview.Api;

public enum CallerRefusal
{
    WrongEvent,
    WrongEnvironment,
    RepositoryNotAllowed,
    WrongWorkflow,
    UnreleasedCommit,
    NotAPullRequest,
    IncompleteToken,
}

/// <summary>Who's calling, all of it from GitHub's signed claims and none of it from the request.</summary>
public sealed record Caller(string RepositoryId, string Repository, int PullRequest, string RunId, string RunAttempt);

/// <summary>Either a caller or why it was refused.</summary>
public sealed record CallerDecision(Caller? Caller, CallerRefusal? Refusal);

/// <summary>
/// Decides whether a token GitHub signed may use the API (D45). The token only proves a job ran on GitHub: each
/// check below refuses a job that can get a real one, such as a PR's plan job or a job a PR added to its workflow.
/// </summary>
public static partial class CallerPolicy
{
    private const string Workflow = "gergodrazsdik/tf-review/.github/workflows/tf-review.yml@";

    public static CallerDecision Check(IReadOnlyDictionary<string, string> claims, ApiOptions options)
    {
        if (claims.GetValueOrDefault("event_name") != "pull_request")
        {
            return Refuse(CallerRefusal.WrongEvent);
        }

        // Only the review job has this, and the plan job, which runs PR code, doesn't.
        if (claims.GetValueOrDefault("environment") != "review")
        {
            return Refuse(CallerRefusal.WrongEnvironment);
        }

        if (claims.GetValueOrDefault("repository_id") is not { } repositoryId || !options.AllowedRepositoryIds.Contains(repositoryId))
        {
            return Refuse(CallerRefusal.RepositoryNotAllowed);
        }

        if (claims.GetValueOrDefault("job_workflow_ref") is not { } workflow || !workflow.StartsWith(Workflow, StringComparison.Ordinal))
        {
            return Refuse(CallerRefusal.WrongWorkflow);
        }

        // GitHub resolves a fork's commit through its parent, so a SHA under tf-review's name proves nothing by itself.
        // Only the list of released commits does, which also means callers pin a full SHA, not a tag or branch.
        if (!options.ReleasedCommits.Contains(workflow[Workflow.Length..]))
        {
            return Refuse(CallerRefusal.UnreleasedCommit);
        }

        var pullRequest = PullRequestRef().Match(claims.GetValueOrDefault("ref") ?? string.Empty);
        if (!pullRequest.Success || !int.TryParse(pullRequest.Groups[1].Value, out var number))
        {
            return Refuse(CallerRefusal.NotAPullRequest);
        }

        return claims.GetValueOrDefault("repository") is { } repository
            && claims.GetValueOrDefault("run_id") is { } runId
            && claims.GetValueOrDefault("run_attempt") is { } runAttempt
            ? new CallerDecision(new Caller(repositoryId, repository, number, runId, runAttempt), null)
            : Refuse(CallerRefusal.IncompleteToken);
    }

    private static CallerDecision Refuse(CallerRefusal refusal) => new(null, refusal);

    [GeneratedRegex("^refs/pull/([0-9]{1,9})/merge$")]
    private static partial Regex PullRequestRef();
}
