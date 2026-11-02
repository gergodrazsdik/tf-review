using System.Text.Json;
using System.Text.Json.Serialization;

namespace TfReview.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    [JsonStringEnumMemberName("no_findings")]
    NoFindings,

    [JsonStringEnumMemberName("findings")]
    Findings,

    [JsonStringEnumMemberName("not_reviewed")]
    NotReviewed,
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,
}

[JsonConverter(typeof(JsonStringEnumConverter<Reporter>))]
public enum Reporter
{
    [JsonStringEnumMemberName("orchestrator")]
    Orchestrator,

    [JsonStringEnumMemberName("security")]
    Security,

    [JsonStringEnumMemberName("cost")]
    Cost,

    [JsonStringEnumMemberName("conventions")]
    Conventions,

    [JsonStringEnumMemberName("arbiter")]
    Arbiter,

    [JsonStringEnumMemberName("scanner")]
    Scanner,
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewScope>))]
public enum ReviewScope
{
    [JsonStringEnumMemberName("plan")]
    Plan,

    [JsonStringEnumMemberName("conventions")]
    Conventions,

    [JsonStringEnumMemberName("security")]
    Security,

    [JsonStringEnumMemberName("cost")]
    Cost,
}

[JsonConverter(typeof(JsonStringEnumConverter<NotReviewedReason>))]
public enum NotReviewedReason
{
    [JsonStringEnumMemberName("redaction_refused")]
    RedactionRefused,

    [JsonStringEnumMemberName("invalid_output")]
    InvalidOutput,

    [JsonStringEnumMemberName("refused")]
    Refused,

    [JsonStringEnumMemberName("truncated")]
    Truncated,

    [JsonStringEnumMemberName("content_filter")]
    ContentFilter,

    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("timeout")]
    Timeout,

    [JsonStringEnumMemberName("invalid_config")]
    InvalidConfig,

    [JsonStringEnumMemberName("standards_unavailable")]
    StandardsUnavailable,

    [JsonStringEnumMemberName("scanner_failed")]
    ScannerFailed,

    [JsonStringEnumMemberName("plan_too_large")]
    PlanTooLarge,
}

public sealed record Finding(
    [property: JsonPropertyName("resource_address")] string ResourceAddress,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("agent")] Reporter Agent,
    [property: JsonPropertyName("deterministic")] bool Deterministic,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("evidence")] IReadOnlyList<Evidence> Evidence)
{
    [JsonPropertyName("suggestion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suggestion { get; init; }

    /// <summary>Other rules this row also stands for, after an arbiter merged findings about one problem.</summary>
    [JsonPropertyName("related_rules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RelatedRules { get; init; }

    [JsonPropertyName("monthly_cost_delta_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MonthlyCostDeltaUsd { get; init; }
}

public sealed record NotReviewed(
    [property: JsonPropertyName("scope")] ReviewScope Scope,
    [property: JsonPropertyName("reason")] NotReviewedReason Reason);

public sealed record TokenUsage(
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens)
{
    [JsonPropertyName("cached_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CachedInputTokens { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Calls { get; init; }

    [JsonPropertyName("latency_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LatencyMs { get; init; }
}

/// <summary>One change, in the order to look at them, and why it deserves a look if it does.</summary>
public sealed record RiskItem([property: JsonPropertyName("resource_address")] string ResourceAddress)
{
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>What one resource costs a month before and after the plan, in US dollars, at list prices.</summary>
public sealed record ResourceCostChange(
    [property: JsonPropertyName("resource_address")] string ResourceAddress,
    [property: JsonPropertyName("before_usd")] decimal BeforeUsd,
    [property: JsonPropertyName("after_usd")] decimal AfterUsd,
    [property: JsonPropertyName("priced")] bool Priced);

/// <summary>The plan's cost. Totals count only what could be priced, so check for unpriced resources too.</summary>
public sealed record CostSummary(
    [property: JsonPropertyName("before_usd")] decimal BeforeUsd,
    [property: JsonPropertyName("after_usd")] decimal AfterUsd,
    [property: JsonPropertyName("resources")] IReadOnlyList<ResourceCostChange> Resources);

/// <summary>The outcome of one review, as docs/contracts/review-result.schema.json defines it.</summary>
public sealed record ReviewResult(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("verdict")] Verdict Verdict,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("not_reviewed")] IReadOnlyList<NotReviewed> NotReviewed,
    [property: JsonPropertyName("evidence_drops")] IReadOnlyDictionary<string, int> EvidenceDrops)
{
    [JsonPropertyName("standards_commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StandardsCommit { get; init; }

    [JsonPropertyName("standards_repository")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StandardsRepository { get; init; }

    [JsonPropertyName("doc_paths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? DocPaths { get; init; }

    [JsonPropertyName("omitted_docs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OmittedDocs { get; init; }

    [JsonPropertyName("risk_order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RiskItem>? RiskOrder { get; init; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CostSummary? Cost { get; init; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsage? Usage { get; init; }

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>Reads a result file, and throws <see cref="JsonException"/> if a required field is missing.</summary>
    public static ReviewResult Parse(string json) =>
        JsonSerializer.Deserialize<ReviewResult>(json, StrictOptions)
        ?? throw new JsonException("The result file is empty.");

    /// <summary>A review that couldn't look at anything, for one reason.</summary>
    public static ReviewResult Failed(string commit, ReviewScope scope, NotReviewedReason reason) =>
        new(1, commit, Verdict.NotReviewed, [], [new NotReviewed(scope, reason)], new Dictionary<string, int>());
}
