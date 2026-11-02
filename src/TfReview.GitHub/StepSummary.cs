using System.Globalization;
using System.Text;
using TfReview.Core;

namespace TfReview.GitHub;

/// <summary>
/// What a review cost, for the job's step summary. Actions logs and summaries are public here, so this
/// holds counts and names only, never plan text. App Insights takes over the metrics in M3.
/// </summary>
public static class StepSummary
{
    public static string Render(ReviewResult result)
    {
        var summary = new StringBuilder()
            .AppendLine("### tf-review")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Verdict: {CommentRenderer.Code(VerdictName(result.Verdict))}, {result.Findings.Count} finding(s).");

        if (result.Usage is { } usage)
        {
            AppendUsage(summary, usage);
        }
        else
        {
            summary.AppendLine().AppendLine("No model calls.");
        }

        if (result.EvidenceDrops.Count > 0)
        {
            summary.AppendLine().AppendLine("Dropped by the evidence check:");
            foreach (var (reason, count) in result.EvidenceDrops.OrderBy(drop => drop.Key, StringComparer.Ordinal))
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {CommentRenderer.Code(reason)}: {count}");
            }
        }

        return summary.ToString();
    }

    /// <summary>The estimated cost in US dollars, or null for a model with no known price.</summary>
    public static decimal? EstimateCost(TokenUsage usage) =>
        ModelPrices.Estimate(usage.Model, usage.InputTokens, usage.CachedInputTokens ?? 0, usage.OutputTokens);

    private static void AppendUsage(StringBuilder summary, TokenUsage usage)
    {
        var cost = EstimateCost(usage) is { } estimate
            ? string.Create(CultureInfo.InvariantCulture, $"~${estimate:0.0000}")
            : "unknown";

        summary.AppendLine()
            .AppendLine("| Model | Calls | Input tokens | Cached | Output tokens | Estimated cost | Model time |")
            .AppendLine("| --- | --- | --- | --- | --- | --- | --- |")
            .AppendLine(CultureInfo.InvariantCulture, $"| {CommentRenderer.Code(usage.Model ?? "unknown")} | {usage.Calls ?? 0} | {usage.InputTokens} | {usage.CachedInputTokens ?? 0} | {usage.OutputTokens} | {cost} | {(usage.LatencyMs ?? 0) / 1000m:0.0} s |");
    }

    private static string VerdictName(Verdict verdict) => verdict switch
    {
        Verdict.NoFindings => "no_findings",
        Verdict.Findings => "findings",
        Verdict.NotReviewed => "not_reviewed",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };
}
