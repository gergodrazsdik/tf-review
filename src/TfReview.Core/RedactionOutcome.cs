using System.Text.Json.Nodes;

namespace TfReview.Core;

public enum RefusalRule
{
    MalformedJson,
    UnsupportedFormat,
    ErroredPlan,
    MalformedPlan,
    MalformedMarks,
    MalformedUnknown,
    SecretSurvived,
}

/// <summary>The result of redacting a plan: either a plan that's safe to pass on, or a refusal.</summary>
public closed record RedactionOutcome;

public sealed record Redacted(RedactedPlan Plan, int SecretsFound, int FieldsDropped) : RedactionOutcome;

/// <summary>
/// Why a plan was refused. <paramref name="Location"/> names only a section and an index, never an
/// attribute or value, because map keys can hold secrets and this text reaches a public comment.
/// </summary>
public sealed record Refused(RefusalRule Rule, string Location) : RedactionOutcome;

/// <summary>
/// A plan that has been through <see cref="PlanRedactor"/>. Only the redactor can create one, so code
/// that takes this type can't be handed a raw plan by mistake.
/// </summary>
public sealed class RedactedPlan
{
    private readonly JsonObject root;

    internal RedactedPlan(JsonObject root) => this.root = root;

    public string ToCanonicalJson() => CanonicalJson.Write(root);
}
