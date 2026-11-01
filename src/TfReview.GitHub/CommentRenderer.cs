using System.Globalization;
using System.Text;
using TfReview.Core;

namespace TfReview.GitHub;

/// <summary>
/// Renders the PR comment. Everything from a plan or a finding is shown through <see cref="Code"/>, because
/// that text comes from the PR: a resource name could otherwise hold an image, a link or a mention.
/// </summary>
public static class CommentRenderer
{
    public const string Marker = "<!-- tf-review -->";

    private const int MaxRows = 50;
    private const int MaxTextLength = 300;

    public static string Render(ReviewResult result, PlanCounts? counts)
    {
        var comment = new StringBuilder()
            .AppendLine(Marker)
            .AppendLine("### tf-review")
            .AppendLine()
            .AppendLine($"{Headline(result)} for commit `{ShortCommit(result.Commit)}`.");

        if (counts is not null)
        {
            comment.AppendLine().AppendLine(Summarize(counts));
        }

        if (result.Findings.Count > 0)
        {
            comment.AppendLine();
            AppendFindings(comment, result.Findings);
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

    /// <summary>
    /// Shows untrusted text as an inline code span. GitHub doesn't parse markdown, HTML, mentions or links
    /// inside one, which is more reliable than escaping each character that could mean something.
    /// </summary>
    public static string Code(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        foreach (var rune in text.EnumerateRunes())
        {
            // Control and format characters can break out of a table row or disguise text.
            var category = Rune.GetUnicodeCategory(rune);
            builder.Append(category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? new Rune(' ')
                : rune);

            if (builder.Length >= MaxTextLength)
            {
                builder.Append('…');
                break;
            }
        }

        var clean = builder.ToString().Trim();
        if (clean.Length == 0)
        {
            return "` `";
        }

        // The fence has to be longer than any run of backticks inside, and a pipe still ends a table cell.
        var fence = new string('`', LongestRun(clean, '`') + 1);
        var padded = clean.StartsWith('`') || clean.EndsWith('`') ? $" {clean} " : clean;
        return $"{fence}{padded.Replace("|", "\\|", StringComparison.Ordinal)}{fence}";
    }

    private static void AppendFindings(StringBuilder comment, IReadOnlyList<Finding> findings)
    {
        comment.AppendLine("| Severity | Resource | Rule | Finding | Source |");
        comment.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var finding in findings.Take(MaxRows))
        {
            comment.AppendLine(
                $"| {Name(finding.Severity)} | {Code(finding.ResourceAddress)} | {Code(finding.Rule)} | {Code(finding.Title)} | {Source(finding)} |");
        }

        if (findings.Count > MaxRows)
        {
            comment.AppendLine().AppendLine($"{findings.Count - MaxRows} more finding(s) aren't shown.");
        }
    }

    private static string Headline(ReviewResult result) => result.Verdict switch
    {
        Verdict.NoFindings => "**No findings**",
        Verdict.Findings => $"**{result.Findings.Count} finding(s)**",
        Verdict.NotReviewed => "**Not reviewed**",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static string ShortCommit(string commit) => commit.Length >= 7 ? commit[..7] : commit;

    private static string Summarize(PlanCounts counts)
    {
        var actions = counts.Actions
            .Where(action => action.Value > 0)
            .Select(action => $"{action.Value} {action.Key}");
        return $"Plan: {counts.ResourceChanges} resource change(s) ({string.Join(", ", actions)}).";
    }

    private static string Source(Finding finding) => finding.Deterministic ? "policy" : "model";

    private static string Name(Severity severity) => severity switch
    {
        Severity.Low => "low",
        Severity.Medium => "medium",
        Severity.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };

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
        NotReviewedReason.InvalidConfig => "the config is invalid",
        NotReviewedReason.StandardsUnavailable => "the standards couldn't be fetched",
        NotReviewedReason.ScannerFailed => "a policy check failed to run",
        NotReviewedReason.PlanTooLarge => "the plan is too large for one model call",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var value in text)
        {
            current = value == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
