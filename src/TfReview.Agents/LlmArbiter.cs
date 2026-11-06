using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TfReview.Core;

namespace TfReview.Agents;

/// <summary>
/// Proposes merging findings about one problem, and an order within each severity (D36). It has no way to
/// remove a finding: on a live run it dropped a correct model finding, and a redundant row costs less than
/// a missing one. Code enforces the rest, because plan text can steer a model: it can't add a finding, merge
/// across resources, merge a model opinion into a deterministic finding, which could make it blocking, or
/// rank a lower severity above a higher one. Without a usable answer the rule-based result stands.
/// </summary>
public sealed partial class LlmArbiter(IChatClient chat, IArbiter? fallback = null, TimeSpan? attemptTimeout = null) : IArbiter
{
    public const string Name = "arbiter";

    /// <summary>Above this many rows the model isn't asked, which bounds the call: about 60 tokens a row.</summary>
    public const int MaxFindings = 150;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "groups": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "finding_ids": { "type": "array", "items": { "type": "string" } } },
                "required": ["finding_ids"],
                "additionalProperties": false
              }
            },
            "order": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["groups", "order"],
          "additionalProperties": false
        }
        """;

    private readonly IArbiter fallback = fallback ?? new RuleArbiter();

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromMinutes(1);

    public async Task<Arbitration> ArbitrateAsync(
        IReadOnlyList<Finding> findings, CondensedPlan plan, IReadOnlyList<string>? riskOrder = null, CancellationToken cancellationToken = default)
    {
        var merged = await fallback.ArbitrateAsync(findings, plan, riskOrder, cancellationToken);

        // With fewer than two there's nothing to merge or order. With very many, a plan made to produce them would make one
        // huge call, which a retry doubles (SEC-5), and the rules' merge and order already stand on their own.
        if (merged.Findings.Count < 2 || merged.Findings.Count > MaxFindings)
        {
            return merged;
        }

        var prompt = Prompts.Load(Name);
        var byId = merged.Findings.Select((finding, index) => (Id: $"F{index + 1}", Finding: finding)).ToDictionary(item => item.Id, item => item.Finding, StringComparer.Ordinal);
        var result = await StructuredCall.RunAsync(
            chat, Name, prompt.Body, UserMessage(byId), Schema, StructuredCall.ParseWire<WireOutput>, timeout, cancellationToken);

        if (result.Value is not { } answer)
        {
            return merged with { Calls = result.Calls, PromptVersion = prompt.Version };
        }

        return Apply(answer, byId, riskOrder) with { Calls = result.Calls, PromptVersion = prompt.Version };
    }

    private static string UserMessage(IReadOnlyDictionary<string, Finding> byId)
    {
        var list = new JsonArray(
        [
            .. byId.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.Key,
                ["resource_address"] = item.Value.ResourceAddress,
                ["rule"] = item.Value.Rule,
                ["severity"] = SeverityName(item.Value.Severity),
                ["source"] = item.Value.Deterministic ? "scanner" : "model",
                ["title"] = item.Value.Title,
            }),
        ]);

        return $"""
            <findings>
            {StructuredCall.Escape(CanonicalJson.Write(list))}
            </findings>
            """;
    }

    private static string SeverityName(Severity severity) => severity switch
    {
        Severity.Low => "low",
        Severity.Medium => "medium",
        _ => "high",
    };

    private static Arbitration Apply(WireOutput answer, IReadOnlyDictionary<string, Finding> byId, IReadOnlyList<string>? riskOrder)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string Id, Finding Finding)>();

        foreach (var group in answer.Groups)
        {
            var members = group.FindingIds.Distinct(StringComparer.Ordinal).Where(id => byId.ContainsKey(id) && !claimed.Contains(id)).ToList();
            if (members.Count < 2 || !CanMerge([.. members.Select(id => byId[id])]))
            {
                continue;
            }

            claimed.UnionWith(members);
            rows.Add((members[0], Merge([.. members.Select(id => byId[id])])));
        }

        rows.AddRange(byId.Where(item => !claimed.Contains(item.Key)).Select(item => (item.Key, item.Value)));

        var modelRanks = answer.Order
            .Select((id, index) => (id, index))
            .DistinctBy(rank => rank.id)
            .ToDictionary(rank => rank.id, rank => rank.index, StringComparer.Ordinal);

        Finding[] ordered =
        [
            .. rows
                .OrderByDescending(row => row.Finding.Severity)
                .ThenBy(row => RuleArbiter.RiskRank(row.Finding, riskOrder))
                .ThenBy(row => modelRanks.GetValueOrDefault(row.Id, int.MaxValue))
                .ThenBy(row => row.Finding.ResourceAddress, StringComparer.Ordinal)
                .ThenBy(row => row.Finding.Rule, StringComparer.Ordinal)
                .Select(row => row.Finding),
        ];

        return new Arbitration(Verdict.Findings, ordered);
    }

    private static bool CanMerge(IReadOnlyList<Finding> findings) =>
        findings.Select(finding => finding.ResourceAddress).Distinct(StringComparer.Ordinal).Count() == 1
        && findings.Select(finding => finding.Deterministic).Distinct().Count() == 1;

    // Code picks the row people read: one citing a standard says more than a scanner's rule ID.
    private static Finding Merge(IReadOnlyList<Finding> findings)
    {
        var primary = findings
            .OrderByDescending(finding => DocId().IsMatch(finding.Rule))
            .ThenByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Rule, StringComparer.Ordinal)
            .First();

        return primary with
        {
            Severity = findings.Max(finding => finding.Severity),
            Agent = findings.FirstOrDefault(finding => finding.Agent != Reporter.Scanner)?.Agent ?? Reporter.Scanner,
            Evidence = RuleArbiter.MergeEvidence(findings),
            RelatedRules = [.. findings.Select(finding => finding.Rule).Where(rule => rule != primary.Rule).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Suggestion = primary.Suggestion ?? findings.Select(finding => finding.Suggestion).FirstOrDefault(suggestion => suggestion is not null),
            MonthlyCostDeltaUsd = findings.Select(finding => finding.MonthlyCostDeltaUsd).FirstOrDefault(delta => delta is not null),
        };
    }

    [GeneratedRegex("^(ADR|POL|DES)-[0-9]{3}$")]
    private static partial Regex DocId();

    private sealed record WireOutput(
        [property: JsonPropertyName("groups")] IReadOnlyList<WireGroup> Groups,
        [property: JsonPropertyName("order")] IReadOnlyList<string> Order);

    private sealed record WireGroup([property: JsonPropertyName("finding_ids")] IReadOnlyList<string> FindingIds);
}
