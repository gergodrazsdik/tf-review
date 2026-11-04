using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TfReview.Core;

namespace TfReview.GitHub;

/// <summary>
/// Renders the PR comment. Everything from a plan or a finding is shown through <see cref="Code"/>, because
/// that text comes from the PR: a resource name could otherwise hold an image, a link or a mention.
/// </summary>
public static partial class CommentRenderer
{
    public const string Marker = "<!-- tf-review -->";

    private const int MaxRows = 50;
    private const int MaxUnpriced = 10;
    private const int MaxRiskItems = 5;
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

        if (result.Cost is { } cost)
        {
            AppendCost(comment, cost);
        }

        AppendRisk(comment, result);

        if (result.Findings.Count > 0)
        {
            comment.AppendLine();
            AppendFindings(comment, result);
        }

        foreach (var missing in result.NotReviewed)
        {
            comment.AppendLine().AppendLine(missing.Resources is { Count: > 0 } resources
                ? $"Not reviewed by {Name(missing.Scope)} ({Name(missing.Reason)}): {Listed(resources)}."
                : $"Not reviewed: {Name(missing.Scope)} ({Name(missing.Reason)}).");
        }

        if (result.OmittedDocs is { Count: > 0 } omitted)
        {
            comment.AppendLine().AppendLine($"Not checked, too large for the prompt: {string.Join(", ", omitted.Select(Code))}.");
        }

        // Suggestions can draw on licensed text, and CC-BY asks for credit wherever that happens (D41).
        if (result.References is { } references)
        {
            if (references.Attributions.Count > 0)
            {
                comment.AppendLine().AppendLine($"Reference material: {string.Join("; ", references.Attributions)}.");
            }

            if (references.ProviderDocsMissing)
            {
                comment.AppendLine().AppendLine("No azurerm docs were used: the project's lock file names no azurerm version, or one that isn't indexed.");
            }
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
    // A large plan can leave hundreds out, and the comment has a size limit, so the rest is a count.
    private static string Listed(IReadOnlyList<string> addresses)
    {
        const int Shown = 10;
        var listed = string.Join(", ", addresses.Take(Shown).Select(Code));
        return addresses.Count > Shown ? $"{listed} and {addresses.Count - Shown} more" : listed;
    }

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

    // Only what has a reason: most changes are routine, and a full list would bury the few that aren't.
    private static void AppendRisk(StringBuilder comment, ReviewResult result)
    {
        var worthALook = result.RiskOrder?.Where(item => item.Reason is not null).Take(MaxRiskItems).ToList() ?? [];
        if (worthALook.Count == 0)
        {
            return;
        }

        comment.AppendLine().AppendLine("Look at first:").AppendLine();
        foreach (var item in worthALook)
        {
            comment.AppendLine($"- {Code(item.ResourceAddress)}: {Code(item.Reason!)}");
        }
    }

    private static void AppendCost(StringBuilder comment, CostSummary cost)
    {
        var delta = cost.AfterUsd - cost.BeforeUsd;
        comment.AppendLine().AppendLine(delta == 0
            ? "Monthly cost: no change at list prices, without usage-based charges."
            : $"Monthly cost: **{Signed(delta)}** (from {Money(cost.BeforeUsd)} to {Money(cost.AfterUsd)}), at list prices, without usage-based charges.");

        // A resource that couldn't be priced isn't free, and the line above would otherwise suggest it.
        var unpriced = cost.Resources.Where(resource => !resource.Priced).Select(resource => resource.ResourceAddress).ToList();
        if (unpriced.Count > 0)
        {
            var shown = string.Join(", ", unpriced.Take(MaxUnpriced).Select(Code));
            var more = unpriced.Count > MaxUnpriced ? $" and {unpriced.Count - MaxUnpriced} more" : string.Empty;
            comment.AppendLine().AppendLine($"Couldn't be priced, so not counted: {shown}{more}.");
        }
    }

    private static string Money(decimal amount) => amount.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    private static string Signed(decimal amount) => (amount < 0 ? "-" : "+") + Money(Math.Abs(amount));

    private static void AppendFindings(StringBuilder comment, ReviewResult result)
    {
        var findings = result.Findings;
        comment.AppendLine("| Severity | Resource | Rule | Finding | Suggestion | Source |");
        comment.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var finding in findings.Take(MaxRows))
        {
            var suggestion = finding.Suggestion is null ? string.Empty : Code(finding.Suggestion);
            comment.AppendLine(
                $"| {Name(finding.Severity)} | {Code(finding.ResourceAddress)} | {Rules(finding, result)} | {Code(finding.Title)} | {suggestion} | {Source(finding)} |");
        }

        if (findings.Count > MaxRows)
        {
            comment.AppendLine().AppendLine($"{findings.Count - MaxRows} more finding(s) aren't shown.");
        }
    }

    /// <summary>
    /// A link to the cited doc at the pinned commit, built only from values that match strict patterns,
    /// so nothing from a plan can shape it. Anything else stays plain text in a code span.
    /// </summary>
    private static string Rule(Finding finding, ReviewResult result)
    {
        if (DocId().IsMatch(finding.Rule)
            && result.StandardsRepository is { } repository && Repository().IsMatch(repository)
            && result.StandardsCommit is { } commit && CommitSha().IsMatch(commit)
            && result.DocPaths?.GetValueOrDefault(finding.Rule) is { } path && DocPath().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal))
        {
            return $"[{finding.Rule}](https://github.com/{repository}/blob/{commit}/{path})";
        }

        return Code(finding.Rule);
    }

    private static string Rules(Finding finding, ReviewResult result)
    {
        var rule = Rule(finding, result);
        return finding.RelatedRules is { Count: > 0 } related
            ? $"{rule}, also {string.Join(", ", related.Select(Code))}"
            : rule;
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

    private static string Source(Finding finding) => finding switch
    {
        { Deterministic: false } => "model",
        _ when !DocId().IsMatch(finding.Rule) => "scanner",
        { Agent: Reporter.Scanner } => "policy",
        _ => "policy and model",
    };

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
        NotReviewedReason.ScannerFailed => "a scanner failed to run",
        NotReviewedReason.PlanTooLarge => "the plan is too large",
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

    [GeneratedRegex("^(ADR|POL|DES)-[0-9]{3}$")]
    private static partial Regex DocId();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex Repository();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9_./-]*$")]
    private static partial Regex DocPath();
}
