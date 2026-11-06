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

    /// <summary>
    /// What a large plan's batches didn't reach, by why: a call that failed, and the batch cap, each its own part so one
    /// reason doesn't relabel the other's resources. Empty when the whole slice was reviewed.
    /// </summary>
    public IReadOnlyList<SkippedPart> Skipped { get; init; } = [];
}

/// <summary>Resources an agent didn't review, in address order, and why.</summary>
public sealed record SkippedPart(NotReviewedReason Reason, IReadOnlyList<string> Resources);

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
    /// <summary>
    /// Above this the 30K TPM limit would reject the call, whatever the retries, so a larger slice is reviewed in batches
    /// under it, riskiest first.
    /// </summary>
    public const int MaxUserMessageTokens = 20_000;

    /// <summary>
    /// Batches per agent, one after another so they stay within the TPM limit: about 80K input tokens at most, which
    /// bounds a large plan's cost and time. What's left is reported by resource rather than reviewed.
    /// </summary>
    public const int MaxBatches = 4;

    // Azure's strict mode needs every field required and rejects pattern and minItems, so this can't be
    // finding.schema.json. scripts/llm-smoke.cs proves this shape against the real model.
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "policy_suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "suggestion": { "type": ["string", "null"] }
                },
                "required": ["id", "suggestion"],
                "additionalProperties": false
              }
            },
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
          "required": ["policy_suggestions", "findings"],
          "additionalProperties": false
        }
        """;

    // The cost agent gets no policy findings, so it keeps the shape it had before D51: a field it can only leave empty
    // shouldn't be part of what it's asked.
    private const string CostSchema = """
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
        CancellationToken cancellationToken = default,
        IReadOnlyList<ReferenceDoc>? references = null)
    {
        var prompt = Prompts.Load(profile.Name);
        var calls = new List<AgentCall>();

        // No standard applies, so there's nothing to ask, and no reason to pay for it.
        if (docs.Count == 0)
        {
            return new AgentReviewed(prompt.Version, calls, []);
        }

        // Only findings for the standards it can cite, each with an ID its suggestion answers to.
        var policies = policyFindings.Where(finding => docs.Any(doc => doc.Id == finding.Rule)).ToList();
        var user = UserMessage(plan, docs, policies, costs, references);
        if (TokenEstimate.Of(user) <= MaxUserMessageTokens)
        {
            var result = await this.CallAsync(prompt, docs, user, policies, costs, cancellationToken);
            return result.Value is { } parsed
                ? new AgentReviewed(prompt.Version, result.Calls, parsed.Findings) { Retracted = parsed.Retracted }
                : new AgentFailed(prompt.Version, result.Calls, result.Failure ?? NotReviewedReason.InvalidOutput);
        }

        return await this.ReviewInBatchesAsync(prompt, plan, docs, policies, costs, references, cancellationToken);
    }

    /// <summary>
    /// Splits the plan into batches in the rules' risk order, so deletes, replacements, access and network changes go
    /// first, and the cap cuts the routine end. A batch carries only its own resources' policy findings and cost figures.
    /// </summary>
    public static (IReadOnlyList<IReadOnlyList<string>> Batches, IReadOnlyList<string> Left) Batch(
        CondensedPlan plan, Func<IReadOnlySet<string>, int> estimate, int maxTokens = MaxUserMessageTokens, int maxBatches = MaxBatches)
    {
        var batches = new List<IReadOnlyList<string>>();
        var left = new List<string>();
        var current = new List<string>();

        foreach (var address in RiskOrchestrator.ByRules(plan).Select(item => item.ResourceAddress))
        {
            if (batches.Count == maxBatches)
            {
                left.Add(address);
                continue;
            }

            if (estimate(new HashSet<string>([.. current, address], StringComparer.Ordinal)) <= maxTokens)
            {
                current.Add(address);
                continue;
            }

            // One resource too large for a call on its own can't be reviewed at all.
            if (current.Count == 0)
            {
                left.Add(address);
                continue;
            }

            batches.Add(current);
            current = [];
            if (batches.Count < maxBatches && estimate(new HashSet<string>([address], StringComparer.Ordinal)) <= maxTokens)
            {
                current.Add(address);
            }
            else
            {
                left.Add(address);
            }
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return (batches, left);
    }

    private async Task<AgentOutcome> ReviewInBatchesAsync(
        Prompt prompt,
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        IReadOnlyList<Finding> policies,
        IReadOnlyList<ResourceCostChange>? costs,
        IReadOnlyList<ReferenceDoc>? references,
        CancellationToken cancellationToken)
    {
        (CondensedPlan Plan, List<Finding> Policies, List<ResourceCostChange>? Costs) Part(IReadOnlySet<string> addresses) => (
            plan.OnlyAddresses(addresses),
            [.. policies.Where(finding => addresses.Contains(finding.ResourceAddress))],
            costs is null ? null : [.. costs.Where(cost => addresses.Contains(cost.ResourceAddress))]);

        var (batches, left) = Batch(plan, addresses =>
        {
            var part = Part(addresses);
            return TokenEstimate.Of(UserMessage(part.Plan, docs, part.Policies, part.Costs, references));
        });

        var calls = new List<AgentCall>();
        var findings = new List<Finding>();
        var retracted = 0;
        var failed = new List<string>();
        NotReviewedReason? failure = null;

        foreach (var batch in batches)
        {
            // After a failure the rest isn't tried: the TPM limit or an outage would fail it too.
            if (failure is not null)
            {
                failed.AddRange(batch);
                continue;
            }

            // Past the review's time, the rest is reported rather than started (D54's partial result).
            if (chat.GetService<ReviewClock>()?.Expired == true)
            {
                failed.AddRange(batch);
                failure = NotReviewedReason.Timeout;
                continue;
            }

            var part = Part(new HashSet<string>(batch, StringComparer.Ordinal));
            var result = await this.CallAsync(
                prompt, docs, UserMessage(part.Plan, docs, part.Policies, part.Costs, references), part.Policies, part.Costs, cancellationToken);
            calls.AddRange(result.Calls);
            if (result.Value is { } parsed)
            {
                findings.AddRange(parsed.Findings);
                retracted += parsed.Retracted;
            }
            else
            {
                failed.AddRange(batch);
                failure = result.Failure ?? NotReviewedReason.InvalidOutput;
            }
        }

        // Nothing at all reviewed is the same as one call failing: the whole scope is reported, not each resource.
        if (failed.Count + left.Count == plan.Changes().Count)
        {
            return new AgentFailed(prompt.Version, calls, failure ?? NotReviewedReason.PlanTooLarge);
        }

        // What failed and what the cap left out are told apart, so a rate limit isn't blamed for the cap (BUG-8).
        List<SkippedPart> skipped = [];
        if (failure is { } reason && failed.Count > 0)
        {
            skipped.Add(new SkippedPart(reason, [.. failed.Order(StringComparer.Ordinal)]));
        }

        if (left.Count > 0)
        {
            skipped.Add(new SkippedPart(NotReviewedReason.PlanTooLarge, [.. left.Order(StringComparer.Ordinal)]));
        }

        return new AgentReviewed(prompt.Version, calls, findings) { Retracted = retracted, Skipped = skipped };
    }

    private Task<StructuredResult<ParsedFindings>> CallAsync(
        Prompt prompt,
        IReadOnlyList<StandardsDoc> docs,
        string user,
        IReadOnlyList<Finding> policies,
        IReadOnlyList<ResourceCostChange>? costs,
        CancellationToken cancellationToken) =>
        StructuredCall.RunAsync(
            chat,
            profile.Name,
            SystemMessage(prompt, docs),
            user,
            profile.Reporter == Reporter.Cost ? CostSchema : Schema,
            text => ParseFindings(text, docs, costs, policies),
            timeout,
            cancellationToken);

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
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        IReadOnlyList<Finding> policyFindings,
        IReadOnlyList<ResourceCostChange>? costs,
        IReadOnlyList<ReferenceDoc>? references)
    {
        var policies = new JsonArray(
        [
            .. policyFindings
                .Select((finding, index) => (JsonNode)new JsonObject
                {
                    ["id"] = PolicyId(index),
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

        // Upstream text, which is untrusted like the plan, so it's data in the user message and never part of the
        // instructions. Escaped the same way, so nothing in it can close the fence.
        if (references is { Count: > 0 })
        {
            var material = new JsonArray(
            [
                .. references.OrderBy(doc => doc.Id, StringComparer.Ordinal).Select(doc => (JsonNode)new JsonObject
                {
                    ["source"] = doc.Source,
                    ["title"] = doc.Title,
                    ["heading"] = doc.Heading,
                    ["text"] = doc.Text,
                }),
            ]);

            message.AppendLine("<reference_material>").AppendLine(StructuredCall.Escape(CanonicalJson.Write(material))).AppendLine("</reference_material>").AppendLine();
        }

        return message.AppendLine("<plan_data>").AppendLine(StructuredCall.Escape(plan.ToCanonicalJson())).Append("</plan_data>").ToString();
    }

    private static string PolicyId(int index) => $"P{index + 1}";

    /// <summary>The position an ID names, or -1 for one that wasn't given.</summary>
    private static int PolicyIndex(string id, int count) =>
        id.StartsWith('P') && int.TryParse(id.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= count
            ? number - 1
            : -1;

    /// <summary>The findings in an answer, or null if it doesn't match the wire schema exactly.</summary>
    private ParsedFindings? ParseFindings(
        string text, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<ResourceCostChange>? costs, IReadOnlyList<Finding> policies)
    {
        var output = StructuredCall.ParseWire<WireOutput>(text);
        if (output is null || output.Findings.Any(finding => finding.ResourceAddress.Length == 0 || finding.Title.Trim().Length == 0))
        {
            return null;
        }

        // On C13 the model judged a resource fine and listed it anyway, however the prompt was worded, so the
        // model answers the question and code acts on it. The field comes last in the schema, because when it
        // came first the model committed to true before reasoning, and then wrote a title saying the opposite.
        // A policy finding gets its suggestion by ID and keeps everything else code gave it: the title, evidence and its
        // being deterministic. So a deletion, which has no attribute to point at, gets one too, and the agent can't skip
        // one by not repeating it (C28, C29). The arbiter merges it with the scanner's copy as before.
        var suggested = output.PolicySuggestions
            .Where(item => !string.IsNullOrWhiteSpace(item.Suggestion))
            .Select(item => (Index: PolicyIndex(item.Id, policies.Count), item.Suggestion))
            .Where(item => item.Index >= 0)
            .DistinctBy(item => item.Index)
            .Select(item => policies[item.Index] with { Agent = profile.Reporter, Suggestion = item.Suggestion!.Trim() });

        return new ParsedFindings(
            [
                .. suggested,
                .. output.Findings.Where(finding => finding.BreaksStandard).Select(finding => ToFinding(finding, docs, costs)),
            ],
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

    private sealed record WireOutput(
        [property: JsonPropertyName("findings")] IReadOnlyList<WireFinding> Findings)
    {
        // Absent from the cost agent's answers, which the strict schema then guarantees.
        [JsonPropertyName("policy_suggestions")]
        public IReadOnlyList<WirePolicySuggestion> PolicySuggestions { get; init; } = [];
    }

    private sealed record WirePolicySuggestion(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("suggestion")] string? Suggestion);

    private sealed record ParsedFindings(IReadOnlyList<Finding> Findings, int Retracted);

    private sealed record WireFinding(
        [property: JsonPropertyName("breaks_standard")] bool BreaksStandard,
        [property: JsonPropertyName("resource_address")] string ResourceAddress,
        [property: JsonPropertyName("doc_id")] string DocId,
        [property: JsonPropertyName("attribute")] string Attribute,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("suggestion")] string? Suggestion);
}
