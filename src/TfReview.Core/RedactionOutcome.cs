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
    internal RedactedPlan(JsonObject root) => Root = root;

    internal JsonObject Root { get; }

    /// <summary>What identifies a resource change: its address, plus its deposed key if it has one.</summary>
    internal static string ResourceKey(JsonObject change)
    {
        var address = change["address"]!.GetValue<string>();
        return change["deposed"] is { } deposed ? $"{address} (deposed {deposed.GetValue<string>()})" : address;
    }

    public string ToCanonicalJson() => CanonicalJson.Write(Root);
}
