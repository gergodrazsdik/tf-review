using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TfReview.Core;

namespace TfReview.Agents;

/// <summary>The changes in the order to look at them, and any calls it took to work that out.</summary>
public sealed record RiskRanking(IReadOnlyList<RiskItem> Order, IReadOnlyList<AgentCall> Calls, int? PromptVersion);

/// <summary>
/// Orders a plan's changes by risk (D35). It decides nothing about coverage: code routes resources to agents.
/// A rule puts deletes and replacements first, then access, then network changes, and the model only orders
/// within those and explains. So a bad answer, or plan text steering the model, can't bury a destructive
/// change. Without a usable answer the rules stand alone, because an order isn't worth failing a review for.
/// </summary>
public sealed class RiskOrchestrator(IChatClient chat, TimeSpan? attemptTimeout = null)
{
    public const string Name = "orchestrator";

    private const int Destructive = 0;
    private const int Access = 1;
    private const int Network = 2;
    private const int Other = 3;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "resources": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "resource_address": { "type": "string" },
                  "reason": { "type": ["string", "null"] }
                },
                "required": ["resource_address", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["resources"],
          "additionalProperties": false
        }
        """;

    private static readonly string[] AccessTypes = ["role_assignment", "role_definition", "access_policy", "federated_identity"];

    private static readonly string[] NetworkTypes = ["network_security", "firewall", "public_ip", "route", "private_endpoint"];

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromMinutes(1);

    /// <summary>The order from the rules alone, which is also the fallback.</summary>
    public static IReadOnlyList<RiskItem> ByRules(CondensedPlan plan) => Order(plan.Changes(), new Dictionary<string, int>(), new Dictionary<string, string>());

    public async Task<RiskRanking> RankAsync(CondensedPlan plan, CancellationToken cancellationToken = default)
    {
        var changes = plan.Changes();

        // With one change there's nothing to order, and the rules already say why it might matter.
        if (changes.Count < 2)
        {
            return new RiskRanking(ByRules(plan), [], null);
        }

        var prompt = Prompts.Load(Name);
        var summary = new JsonArray(
        [
            .. changes.Select(change => (JsonNode)new JsonObject
            {
                ["resource_address"] = change.Address,
                ["type"] = change.Type,
                ["actions"] = new JsonArray([.. change.Actions.Select(action => (JsonNode)JsonValue.Create(action)!)]),
            }),
        ]);
        var user = $"""
            <plan_summary>
            {StructuredCall.Escape(CanonicalJson.Write(summary))}
            </plan_summary>
            """;

        var result = await StructuredCall.RunAsync(
            chat, Name, prompt.Body, user, Schema, StructuredCall.ParseWire<WireOutput>, timeout, cancellationToken);

        if (result.Value is not { } answer)
        {
            return new RiskRanking(ByRules(plan), result.Calls, prompt.Version);
        }

        var known = changes.Select(change => change.Address).ToHashSet(StringComparer.Ordinal);
        var wanted = answer.Resources.Where(resource => known.Contains(resource.ResourceAddress)).ToList();
        var ranks = wanted
            .Select((resource, index) => (resource.ResourceAddress, index))
            .DistinctBy(rank => rank.ResourceAddress)
            .ToDictionary(rank => rank.ResourceAddress, rank => rank.index, StringComparer.Ordinal);
        var reasons = wanted
            .Where(resource => !string.IsNullOrWhiteSpace(resource.Reason))
            .DistinctBy(resource => resource.ResourceAddress)
            .ToDictionary(resource => resource.ResourceAddress, resource => resource.Reason!.Trim(), StringComparer.Ordinal);

        return new RiskRanking(Order(changes, ranks, reasons), result.Calls, prompt.Version);
    }

    private static List<RiskItem> Order(
        IReadOnlyList<PlanChange> changes, IReadOnlyDictionary<string, int> ranks, IReadOnlyDictionary<string, string> reasons) =>
    [
        .. changes
            .OrderBy(Tier)
            .ThenBy(change => ranks.GetValueOrDefault(change.Address, int.MaxValue))
            .ThenBy(change => change.Address, StringComparer.Ordinal)
            .Select(change => new RiskItem(change.Address) { Reason = reasons.GetValueOrDefault(change.Address) ?? RuleReason(change) }),
    ];

    private static int Tier(PlanChange change)
    {
        if (change.Actions.Contains("delete", StringComparer.Ordinal))
        {
            return Destructive;
        }

        if (AccessTypes.Any(type => change.Type.Contains(type, StringComparison.Ordinal)))
        {
            return Access;
        }

        return NetworkTypes.Any(type => change.Type.Contains(type, StringComparison.Ordinal)) ? Network : Other;
    }

    private static string? RuleReason(PlanChange change) => Tier(change) switch
    {
        Destructive when change.Actions.Contains("create", StringComparer.Ordinal) => "This plan replaces it.",
        Destructive => "This plan deletes it.",
        Access => "It changes who has access.",
        Network => "It changes network exposure.",
        _ => null,
    };

    private sealed record WireOutput([property: JsonPropertyName("resources")] IReadOnlyList<WireResource> Resources);

    private sealed record WireResource(
        [property: JsonPropertyName("resource_address")] string ResourceAddress,
        [property: JsonPropertyName("reason")] string? Reason);
}
