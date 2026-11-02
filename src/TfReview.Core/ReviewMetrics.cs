using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TfReview.Core;

/// <summary>
/// What the service reports about its reviews. Tags are names and counts only, never plan text or a model's words,
/// and they stay few, because each new combination of them is a new series in App Insights.
/// </summary>
public static class ReviewMetrics
{
    public const string MeterName = "TfReview";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("tfreview.llm.input_tokens", "{token}");
    private static readonly Counter<long> CachedInputTokens = Meter.CreateCounter<long>("tfreview.llm.cached_input_tokens", "{token}");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("tfreview.llm.output_tokens", "{token}");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>("tfreview.llm.cost", "USD");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("tfreview.llm.latency", "ms");
    private static readonly Counter<long> Reviews = Meter.CreateCounter<long>("tfreview.reviews", "{review}");
    private static readonly Counter<long> Findings = Meter.CreateCounter<long>("tfreview.findings", "{finding}");
    private static readonly Counter<long> EvidenceDrops = Meter.CreateCounter<long>("tfreview.evidence_drops", "{finding}");
    private static readonly Counter<long> NotReviewedScopes = Meter.CreateCounter<long>("tfreview.not_reviewed", "{scope}");

    /// <summary>One model call. A model with no known price reports its tokens and no cost.</summary>
    public static void RecordCall(
        string agent, int? promptVersion, string? model, string finish, long inputTokens, long cachedInputTokens, long outputTokens, TimeSpan latency)
    {
        var tags = new TagList
        {
            { "agent", agent },
            { "prompt_version", promptVersion },
            { "model", model ?? "none" },
            { "finish", finish },
        };

        InputTokens.Add(inputTokens, tags);
        CachedInputTokens.Add(cachedInputTokens, tags);
        OutputTokens.Add(outputTokens, tags);
        Latency.Record(latency.TotalMilliseconds, tags);
        if (ModelPrices.Estimate(model, inputTokens, cachedInputTokens, outputTokens) is { } cost)
        {
            Cost.Add((double)cost, tags);
        }
    }

    /// <summary>A finished review's outcome.</summary>
    public static void RecordResult(ReviewResult result, string repository)
    {
        Reviews.Add(1, new TagList { { "verdict", result.Verdict.ToString() }, { "repository", repository } });

        foreach (var finding in result.Findings)
        {
            Findings.Add(1, new TagList { { "agent", finding.Agent.ToString() }, { "severity", finding.Severity.ToString() } });
        }

        foreach (var (reason, count) in result.EvidenceDrops)
        {
            EvidenceDrops.Add(count, new TagList { { "reason", reason } });
        }

        foreach (var missing in result.NotReviewed)
        {
            NotReviewedScopes.Add(1, new TagList { { "scope", missing.Scope.ToString() }, { "reason", missing.Reason.ToString() } });
        }
    }
}
