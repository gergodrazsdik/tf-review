using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents;

/// <summary>An agent's references, and the model calls it took to choose them.</summary>
public sealed record ReferenceSelection(ReferenceRetrieval Retrieval, IReadOnlyList<AgentCall> Calls, int? PromptVersion);

/// <summary>
/// Chooses an agent's reference material (D49). Search finds the relevant docs, but often not first: offline, about
/// two thirds of them were in the top candidates and only a fifth in the few a budget holds. So the search returns
/// more candidates than fit, the model picks the most useful by title and opening, and code fills the budget in that
/// order. The model only orders background, which can't create a finding (D42), and if it fails the search's own
/// order stands.
/// </summary>
public sealed partial class ReferenceSelector(IReferenceRetriever retriever, IChatClient? chat, TimeSpan? attemptTimeout = null)
{
    public const string Name = "reranker";

    /// <summary>Candidates of each kind the model chooses from.</summary>
    public const int Pool = 15;

    private const int MaxPicks = 6;
    private const int OpeningCharacters = 300;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "ids": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["ids"],
          "additionalProperties": false
        }
        """;

    // Where a PR's own words live, or what every resource sets.
    private static readonly HashSet<string> HiddenAttributes = new(StringComparer.Ordinal)
    {
        "name", "tags", "description", "location", "resource_group_name",
    };

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromSeconds(45);

    /// <param name="query">The search, whose budget applies to what's chosen.</param>
    /// <param name="topics">What the agent reviews.</param>
    /// <param name="slice">The changes it reviews, which the model sees only as facts.</param>
    /// <param name="cancellationToken">Stops the selection.</param>
    public async Task<ReferenceSelection> SelectAsync(
        ReferenceQuery query, IReadOnlyList<string> topics, CondensedPlan slice, CancellationToken cancellationToken = default)
    {
        if (chat is null)
        {
            return new ReferenceSelection(await retriever.RetrieveAsync(query, cancellationToken), [], null);
        }

        var pool = await retriever.RetrieveAsync(query with { TokenBudget = int.MaxValue, Candidates = Pool }, cancellationToken);
        if (pool.Docs.Count == 0)
        {
            return new ReferenceSelection(pool, [], null);
        }

        var prompt = Prompts.Load(Name);
        var candidates = new JsonArray(
        [
            .. pool.Docs.Select(doc => (JsonNode)new JsonObject
            {
                ["id"] = doc.Id,
                ["source"] = doc.Source,
                ["title"] = doc.Title,
                ["heading"] = doc.Heading,
                ["opening"] = doc.Text.Length > OpeningCharacters ? doc.Text[..OpeningCharacters] : doc.Text,
            }),
        ]);
        var user = $"""
            <review_topics>
            {string.Join(", ", topics)}
            </review_topics>

            <changes>
            {StructuredCall.Escape(CanonicalJson.Write(Facts(slice)))}
            </changes>

            <candidates>
            {StructuredCall.Escape(CanonicalJson.Write(candidates))}
            </candidates>
            """;

        var result = await StructuredCall.RunAsync(
            chat, Name, prompt.Body, user, Schema, StructuredCall.ParseWire<WireOutput>, timeout, cancellationToken);

        // Only what was offered, each once. What the model leaves out still follows, in the search's order, so a short
        // or empty answer still fills the budget.
        var byId = pool.Docs.ToDictionary(doc => doc.Id, StringComparer.Ordinal);
        var picked = (result.Value?.Ids ?? []).Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).Take(MaxPicks).Select(id => byId[id]).ToList();
        var order = picked.Concat(pool.Docs.Where(doc => !picked.Contains(doc))).ToList();

        return new ReferenceSelection(pool with { Docs = Fill(order, query.TokenBudget) }, result.Calls, prompt.Version);
    }

    /// <summary>Takes docs in order while they fit, skipping any too big for what's left.</summary>
    public static IReadOnlyList<ReferenceDoc> Fill(IEnumerable<ReferenceDoc> order, int budget)
    {
        var chosen = new List<ReferenceDoc>();
        foreach (var doc in order)
        {
            var tokens = TokenEstimate.Of(doc.Text);
            if (tokens <= budget)
            {
                chosen.Add(doc);
                budget -= tokens;
            }
        }

        return chosen;
    }

    /// <summary>What the model is shown of the changes: no addresses, names, tags or free text, only types and short values.</summary>
    internal static JsonArray Facts(CondensedPlan slice)
    {
        var facts = new JsonArray();
        foreach (var (_, resource) in JsonNode.Parse(slice.ToCanonicalJson())!["resource_changes"]!.AsObject())
        {
            if (resource is null)
            {
                continue;
            }

            var set = new JsonObject();
            if (resource["change"]?["after"] is JsonObject after)
            {
                foreach (var (name, value) in after)
                {
                    if (!HiddenAttributes.Contains(name) && IsSet(value))
                    {
                        // Short scalars carry the meaning, such as a port or a role. Anything else is only named.
                        set[name] = value is JsonValue scalar && ShortValue().IsMatch(scalar.ToString()) ? scalar.DeepClone() : null;
                    }
                }
            }

            facts.Add(new JsonObject
            {
                ["type"] = resource["type"]?.GetValue<string>(),
                ["actions"] = resource["change"]?["actions"]?.DeepClone(),
                ["attributes"] = set,
            });
        }

        return facts;
    }

    private static bool IsSet(JsonNode? value) => value switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject item => item.Count > 0,
        JsonValue scalar => scalar.ToString() is { Length: > 0 } text && text != "false",
        _ => false,
    };

    // Up to 32 characters, of the kind ports, SKUs, roles and flags use.
    [GeneratedRegex("^[A-Za-z0-9_.:/ *-]{1,32}$")]
    private static partial Regex ShortValue();

    private sealed record WireOutput([property: JsonPropertyName("ids")] IReadOnlyList<string> Ids);
}
