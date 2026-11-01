using System.Text;
using TfReview.Core;

namespace TfReview.GitHub;

/// <summary>
/// Renders the PR comment. It only uses numbers and fixed text so far, so nothing from a plan or a
/// model reaches the comment; findings' free text needs escaping before it can be shown here.
/// </summary>
public static class CommentRenderer
{
    public const string Marker = "<!-- tf-review -->";

    public static string Render(ReviewResult result, PlanCounts? counts)
    {
        var comment = new StringBuilder()
            .AppendLine(Marker)
            .AppendLine("### tf-review")
            .AppendLine()
            .AppendLine($"{Headline(result)} for commit `{result.Commit[..7]}`.");

        if (counts is not null)
        {
            comment.AppendLine().AppendLine(Summarize(counts));
        }

        foreach (var missing in result.NotReviewed)
        {
            comment.AppendLine().AppendLine($"Not reviewed: {Name(missing.Scope)} ({Name(missing.Reason)}).");
        }

        return comment
            .AppendLine()
            .AppendLine("This review is advisory, and finding nothing doesn't mean a change is approved.")
            .ToString();
    }

    private static string Headline(ReviewResult result) => result.Verdict switch
    {
        Verdict.NoFindings => "**No findings**",
        Verdict.Findings => $"**{result.Findings.Count} finding(s)**",
        Verdict.NotReviewed => "**Not reviewed**",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static string Summarize(PlanCounts counts)
    {
        var actions = counts.Actions
            .Where(action => action.Value > 0)
            .Select(action => $"{action.Value} {action.Key}");
        return $"Plan: {counts.ResourceChanges} resource change(s) ({string.Join(", ", actions)}).";
    }

    private static string Name(ReviewScope scope) => scope switch
    {
        ReviewScope.Plan => "the whole plan",
        ReviewScope.Conventions => "conventions",
        ReviewScope.Security => "security",
        ReviewScope.Cost => "cost",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static string Name(NotReviewedReason reason) => reason switch
    {
        NotReviewedReason.RedactionRefused => "redaction refused the plan",
        NotReviewedReason.InvalidOutput => "the model's output was invalid",
        NotReviewedReason.Refused => "the model refused",
        NotReviewedReason.Truncated => "the model's output was cut off",
        NotReviewedReason.ContentFilter => "a content filter blocked it",
        NotReviewedReason.RateLimited => "rate limited",
        NotReviewedReason.Unavailable => "unavailable",
        NotReviewedReason.Timeout => "timed out",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
