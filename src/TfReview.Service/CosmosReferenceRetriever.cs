using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;
using Microsoft.Azure.Cosmos;
using OpenAI;
using OpenAI.Embeddings;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Service;

/// <summary>Turns a query into a vector, through the same deployment indexing used, which has to match.</summary>
public interface IQueryEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reference chunks from the index version in use (D44). Provider docs are filtered to the slice's types and the lock
/// file's version, and everything else, which isn't about one type, is found by search alone. Both are ranked, and
/// they share one budget, separate from the team's rules.
/// </summary>
public sealed partial class CosmosReferenceRetriever(Container container, IQueryEmbedder embedder) : IReferenceRetriever
{
    /// <summary>Candidates per kind before the budget cuts them, which is more than a budget ever holds.</summary>
    private const int Candidates = 8;

    private const int MaxSearchTerms = 12;

    private const string Fields = "c.id, c.source, c.title, c.heading, c.text, c.source_url, c.license, c.attribution, c.applies_to";

    // Words too common in these docs to say what a query is about.
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "azurerm", "resource", "resources", "the", "and", "for", "with", "changed", "attributes", "types", "type",
    };

    public async Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default)
    {
        if (await this.ActiveVersionAsync(cancellationToken) is not { } version)
        {
            return ReferenceRetrieval.None;
        }

        var metadataOnly = query.Ranking == ReferenceRanking.Metadata;
        float[] vector = metadataOnly ? [] : await embedder.EmbedAsync(query.Text, cancellationToken);
        var terms = SearchTerms(query.Text);

        var provider = query.ProviderVersion is { } providerVersion && query.ResourceTypes.Count > 0
            ? await this.SearchAsync(
                version,
                // A resource's outputs, which a review of its inputs never needs, and which crowd out what it does.
                "c.provider_version = @providerVersion AND ARRAY_CONTAINS(@types, c.applies_to[0]) AND c.heading != 'Attributes Reference'",
                vector,
                terms,
                query.Ranking,
                new Dictionary<string, object> { ["@providerVersion"] = providerVersion, ["@types"] = query.ResourceTypes.ToArray() },
                cancellationToken)
            : [];
        IReadOnlyList<ReferenceDoc> general = metadataOnly
            ? []
            : await this.SearchAsync(version, "ARRAY_LENGTH(c.applies_to) = 0", vector, terms, query.Ranking, [], cancellationToken);

        // Alternating keeps either kind from taking the whole budget. Policies and framework pages go first, because they
        // say what's expected of a resource, where provider docs mostly say how to write it.
        var chosen = new List<ReferenceDoc>();
        var remaining = query.TokenBudget;
        foreach (var doc in Interleave(general, provider).DistinctBy(doc => doc.Id))
        {
            var tokens = TokenEstimate.Of(doc.Text);
            if (tokens <= remaining)
            {
                chosen.Add(doc);
                remaining -= tokens;
            }
        }

        // Missing when the lock file names no version, or one that isn't indexed. Never another version's docs instead.
        var providerMissing = query.ResourceTypes.Count > 0
            && (query.ProviderVersion is null
                || (provider.Count == 0 && !await this.HasProviderVersionAsync(version, query.ProviderVersion, cancellationToken)));
        return new ReferenceRetrieval([.. chosen.OrderBy(doc => doc.Id, StringComparer.Ordinal)], version, providerMissing);
    }

    /// <summary>The words worth a full-text match: identifiers and words, without the ones every chunk has.</summary>
    internal static IReadOnlyList<string> SearchTerms(string text) =>
    [
        .. Word().Matches(text).Select(match => match.Value.ToLowerInvariant())
            .SelectMany(word => word.Split('_', StringSplitOptions.RemoveEmptyEntries))
            .Where(word => word.Length > 2 && !CommonWords.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSearchTerms),
    ];

    private static IEnumerable<ReferenceDoc> Interleave(IReadOnlyList<ReferenceDoc> first, IReadOnlyList<ReferenceDoc> second)
    {
        for (var index = 0; index < Math.Max(first.Count, second.Count); index++)
        {
            if (index < first.Count)
            {
                yield return first[index];
            }

            if (index < second.Count)
            {
                yield return second[index];
            }
        }
    }

    private async Task<string?> ActiveVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await container.ReadItemStreamAsync("active", new PartitionKey("state"), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken))?["active_version"]?.GetValue<string>();
    }

    private async Task<bool> HasProviderVersionAsync(string version, string providerVersion, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT TOP 1 VALUE c.id FROM c WHERE c.provider_version = @providerVersion")
            .WithParameter("@providerVersion", providerVersion);
        return (await this.ReadAsync(query, version, cancellationToken)).Count > 0;
    }

    private async Task<IReadOnlyList<ReferenceDoc>> SearchAsync(
        string version,
        string filter,
        float[] vector,
        IReadOnlyList<string> terms,
        ReferenceRanking ranking,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        // FullTextScore takes its terms as arguments, so each is a parameter, never text spliced into the query.
        var order = ranking switch
        {
            ReferenceRanking.Metadata => "ORDER BY c.id",
            ReferenceRanking.Hybrid when terms.Count > 0 =>
                $"ORDER BY RANK RRF(VectorDistance(c.embedding, @vector), FullTextScore(c.text, {string.Join(", ", terms.Select((_, index) => $"@term{index}"))}))",
            _ => "ORDER BY VectorDistance(c.embedding, @vector)",
        };

        var query = new QueryDefinition($"SELECT TOP {Candidates} {Fields} FROM c WHERE {filter} {order}");
        if (ranking != ReferenceRanking.Metadata)
        {
            query = query.WithParameter("@vector", vector);
        }
        foreach (var (name, value) in parameters)
        {
            query = query.WithParameter(name, value);
        }

        if (order.Contains("FullTextScore", StringComparison.Ordinal))
        {
            for (var index = 0; index < terms.Count; index++)
            {
                query = query.WithParameter($"@term{index}", terms[index]);
            }
        }

        return [.. (await this.ReadAsync(query, version, cancellationToken)).Select(ToDoc)];
    }

    private async Task<List<JsonNode>> ReadAsync(QueryDefinition query, string version, CancellationToken cancellationToken)
    {
        var items = new List<JsonNode>();
        using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(version) });
        while (iterator.HasMoreResults)
        {
            using var page = await iterator.ReadNextAsync(cancellationToken);
            page.EnsureSuccessStatusCode();
            var body = await JsonNode.ParseAsync(page.Content, cancellationToken: cancellationToken);
            items.AddRange((body?["Documents"]?.AsArray() ?? []).OfType<JsonNode>());
        }

        return items;
    }

    private static ReferenceDoc ToDoc(JsonNode item) => new(
        Text(item, "id"),
        Text(item, "source"),
        Text(item, "title"),
        Text(item, "heading"),
        Text(item, "text"),
        Text(item, "source_url"),
        Text(item, "license"),
        Text(item, "attribution"),
        [.. (item["applies_to"]?.AsArray() ?? []).Select(type => type!.GetValue<string>())]);

    private static string Text(JsonNode item, string name) => item[name]?.GetValue<string>() ?? string.Empty;

    [GeneratedRegex("[A-Za-z][A-Za-z0-9_]+")]
    private static partial Regex Word();
}

/// <summary>The embedding deployment, through Entra ID, since key auth is off on the resource.</summary>
public sealed class AzureQueryEmbedder(EmbeddingClient client) : IQueryEmbedder
{
    private const string Scope = "https://ai.azure.com/.default";

    public static AzureQueryEmbedder Create(Uri endpoint, string deployment, TokenCredential credential)
    {
        var options = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromSeconds(30) };
#pragma warning disable OPENAI001 // The authentication policy constructor is experimental.
        var client = new OpenAIClient(new BearerTokenPolicy(credential, Scope), options);
#pragma warning restore OPENAI001
        return new AzureQueryEmbedder(client.GetEmbeddingClient(deployment));
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        (await client.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken)).Value.ToFloats().ToArray();
}
