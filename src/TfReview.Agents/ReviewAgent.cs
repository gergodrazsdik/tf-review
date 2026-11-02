using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents;

/// <summary>One model call, for the logs and the cost metrics. It holds no prompt or answer text.</summary>
public sealed record AgentCall(int Attempt, string Finish, long InputTokens, long OutputTokens, long CachedInputTokens, TimeSpan Latency);

public closed record AgentOutcome(int PromptVersion, IReadOnlyList<AgentCall> Calls);

/// <summary>The model answered. The findings still have to pass the evidence check.</summary>
public sealed record AgentReviewed(int PromptVersion, IReadOnlyList<AgentCall> Calls, IReadOnlyList<Finding> Findings)
    : AgentOutcome(PromptVersion, Calls)
{
    /// <summary>Resources the model listed while saying they don't break the standard.</summary>
    public int Retracted { get; init; }
}

/// <summary>The slice wasn't reviewed, and the comment says so instead of guessing.</summary>
public sealed record AgentFailed(int PromptVersion, IReadOnlyList<AgentCall> Calls, NotReviewedReason Reason)
    : AgentOutcome(PromptVersion, Calls);

/// <summary>
/// Reviews a plan against the standards a profile retrieves. Plan text only ever reaches the user message, and
/// the agent has no tools. The model chooses the resource, document, attribute and wording; code sets the
/// rule, severity, reporter, evidence and any cost figure, so a model can't claim to be deterministic, raise
/// a severity or invent a price.
/// </summary>
public sealed class ReviewAgent(IChatClient chat, AgentProfile profile, TimeSpan? attemptTimeout = null)
{
    // Above this the 30K TPM limit would reject the call, whatever the retries. M4 splits large plans.
    private const int MaxUserMessageTokens = 20_000;

    // Azure's strict mode needs every field required and rejects pattern and minItems, so this can't be
    // finding.schema.json. scripts/llm-smoke.cs proves this shape against the real model.
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "resource_address": { "type": "string" },
                  "doc_id": { "type": "string" },
                  "attribute": { "type": "string" },
                  "title": { "type": "string" },
                  "suggestion": { "type": ["string", "null"] },
                  "breaks_standard": { "type": "boolean" }
                },
                "required": ["resource_address", "doc_id", "attribute", "title", "suggestion", "breaks_standard"],
                "additionalProperties": false
              }
            }
          },
          "required": ["findings"],
          "additionalProperties": false
        }
        """;

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromMinutes(3);

    public async Task<AgentOutcome> ReviewAsync(
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        IReadOnlyList<Finding> policyFindings,
        IReadOnlyList<ResourceCostChange>? costs = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = Prompts.Load(profile.Name);
        var calls = new List<AgentCall>();

        // No standard applies, so there's nothing to ask, and no reason to pay for it.
        if (docs.Count == 0)
        {
            return new AgentReviewed(prompt.Version, calls, []);
        }

        var user = UserMessage(plan, docs, policyFindings, costs);
        if (TokenEstimate.Of(user) > MaxUserMessageTokens)
        {
            return new AgentFailed(prompt.Version, calls, NotReviewedReason.PlanTooLarge);
        }

        var result = await StructuredCall.RunAsync(
            chat,
            profile.Name,
            SystemMessage(prompt, docs),
            user,
            Schema,
            text => ParseFindings(text, docs, costs),
            timeout,
            cancellationToken);

        return result.Value is { } parsed
            ? new AgentReviewed(prompt.Version, result.Calls, parsed.Findings) { Retracted = parsed.Retracted }
            : new AgentFailed(prompt.Version, result.Calls, result.Failure ?? NotReviewedReason.InvalidOutput);
    }

    /// <summary>The standards are sorted and nothing here varies per run, so the prefix can be cached from M2.</summary>
    private static string SystemMessage(Prompt prompt, IReadOnlyList<StandardsDoc> docs)
    {
        var system = new StringBuilder(prompt.Body).AppendLine();
        foreach (var doc in docs.OrderBy(doc => doc.Id, StringComparer.Ordinal))
        {
            system.AppendLine()
                .AppendLine($"<standard id=\"{doc.Id}\" applies_to=\"{string.Join(' ', doc.AppliesTo)}\">")
                .AppendLine(doc.Body)
                .AppendLine("</standard>");
        }

        return system.ToString();
    }

    /// <summary>Plan text and policy messages come from the PR. An agent only sees findings for the standards it can cite.</summary>
    private static string UserMessage(
        CondensedPlan plan, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<Finding> policyFindings, IReadOnlyList<ResourceCostChange>? costs)
    {
        var policies = new JsonArray(
        [
            .. policyFindings
                .Where(finding => docs.Any(doc => doc.Id == finding.Rule))
                .Select(finding => (JsonNode)new JsonObject
                {
                    ["resource_address"] = finding.ResourceAddress,
                    ["doc_id"] = finding.Rule,
                    ["title"] = finding.Title,
                }),
        ]);

        var message = new StringBuilder()
            .AppendLine("<policy_findings>")
            .AppendLine(StructuredCall.Escape(CanonicalJson.Write(policies)))
            .AppendLine("</policy_findings>")
            .AppendLine();

        if (costs is not null)
        {
            var figures = new JsonArray(
            [
                .. costs.Where(cost => cost.Priced).Select(cost => (JsonNode)new JsonObject
                {
                    ["resource_address"] = cost.ResourceAddress,
                    ["before_usd"] = cost.BeforeUsd,
                    ["after_usd"] = cost.AfterUsd,
                    ["delta_usd"] = cost.AfterUsd - cost.BeforeUsd,

                    // Worked out here, because the model got ADR-003's share rule wrong doing it itself.
                    ["increase_percent"] = cost.BeforeUsd > 0 ? Math.Round((cost.AfterUsd - cost.BeforeUsd) / cost.BeforeUsd * 100, 1) : null,
                }),
            ]);

            message.AppendLine("<cost_data>").AppendLine(StructuredCall.Escape(CanonicalJson.Write(figures))).AppendLine("</cost_data>").AppendLine();
        }

        return message.AppendLine("<plan_data>").AppendLine(StructuredCall.Escape(plan.ToCanonicalJson())).Append("</plan_data>").ToString();
    }

    /// <summary>The findings in an answer, or null if it doesn't match the wire schema exactly.</summary>
    private ParsedFindings? ParseFindings(string text, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<ResourceCostChange>? costs)
    {
        var output = StructuredCall.ParseWire<WireOutput>(text);
        if (output is null || output.Findings.Any(finding => finding.ResourceAddress.Length == 0 || finding.Title.Trim().Length == 0))
        {
            return null;
        }

        // On C13 the model judged a resource fine and listed it anyway, however the prompt was worded, so the
        // model answers the question and code acts on it. The field comes last in the schema, because when it
        // came first the model committed to true before reasoning, and then wrote a title saying the opposite.
        return new ParsedFindings(
            [.. output.Findings.Where(finding => finding.BreaksStandard).Select(finding => ToFinding(finding, docs, costs))],
            output.Findings.Count(finding => !finding.BreaksStandard));
    }

    private Finding ToFinding(WireFinding wire, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<ResourceCostChange>? costs)
    {
        // An unknown document gets a placeholder severity, and the evidence check then drops the finding.
        var severity = docs.FirstOrDefault(doc => doc.Id == wire.DocId)?.Severity ?? Severity.Low;

        // Models like to write tags.environment. Pointers stop at the top-level attribute, as the policies' do.
        var attribute = wire.Attribute.Split(['.', '/', '['], 2)[0].Trim();
        Evidence[] evidence = attribute.Length == 0
            ? [new DocEvidence(wire.DocId)]
            : [new PlanEvidence(CondensedPlan.PointerTo(wire.ResourceAddress, "change", "after", attribute)), new DocEvidence(wire.DocId)];

        return new Finding(wire.ResourceAddress, wire.DocId, severity, profile.Reporter, Deterministic: false, wire.Title.Trim(), evidence)
        {
            Suggestion = string.IsNullOrWhiteSpace(wire.Suggestion) ? null : wire.Suggestion.Trim(),

            // From Infracost, never the model. The evidence check drops a cost finding with no increase here.
            MonthlyCostDeltaUsd = costs?.FirstOrDefault(cost => cost.Priced && cost.ResourceAddress == wire.ResourceAddress) is { } figure
                ? figure.AfterUsd - figure.BeforeUsd
                : null,
        };
    }

    private sealed record WireOutput([property: JsonPropertyName("findings")] IReadOnlyList<WireFinding> Findings);

    private sealed record ParsedFindings(IReadOnlyList<Finding> Findings, int Retracted);

    private sealed record WireFinding(
        [property: JsonPropertyName("breaks_standard")] bool BreaksStandard,
        [property: JsonPropertyName("resource_address")] string ResourceAddress,
        [property: JsonPropertyName("doc_id")] string DocId,
        [property: JsonPropertyName("attribute")] string Attribute,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("suggestion")] string? Suggestion);
}
