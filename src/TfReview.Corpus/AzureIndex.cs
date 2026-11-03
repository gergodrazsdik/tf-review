using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using OpenAI;
using OpenAI.Embeddings;

namespace TfReview.Corpus;

/// <summary>
/// The references container. Chunks are partitioned by corpus version, and one document in its own partition says which
/// version reviews read. Stream APIs throughout, so the Cosmos SDK's own serializer never sees a document.
/// </summary>
public sealed class CosmosReferenceStore(Container container) : IReferenceStore
{
    /// <summary>The partition holding the record of the version in use, which no corpus version can be named.</summary>
    public const string StatePartition = "state";

    public const string ActiveDocument = "active";

    private const int Parallelism = 16;

    public static CosmosReferenceStore Create(string endpoint, TokenCredential credential) => new(
        new CosmosClient(
            endpoint,
            credential,
            new CosmosClientOptions
            {
                ApplicationName = "tf-review-corpus",
                ConnectionMode = ConnectionMode.Gateway,

                // The container's 600 RU/s is the limit on writing, so waiting through throttling is expected.
                MaxRetryAttemptsOnRateLimitedRequests = 60,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromMinutes(10),
            })
        .GetContainer("tfreview", "references"));

    public async Task<string?> ReadActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await container.ReadItemStreamAsync(ActiveDocument, new PartitionKey(StatePartition), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var document = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
        return document?["active_version"]?.GetValue<string>();
    }

    public async Task<IReadOnlyDictionary<string, float[]>> ReadEmbeddingsAsync(string version, CancellationToken cancellationToken = default)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        await foreach (var item in this.QueryAsync("SELECT c.content_hash, c.embedding FROM c", version, cancellationToken))
        {
            if (item["content_hash"]?.GetValue<string>() is { } hash && item["embedding"] is JsonArray embedding)
            {
                vectors[hash] = [.. embedding.Select(value => value!.GetValue<float>())];
            }
        }

        return vectors;
    }

    public async Task UpsertAsync(string version, IReadOnlyList<StoredChunk> chunks, CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (stored, token) =>
            {
                var document = JsonSerializer.SerializeToNode(stored.Chunk)!.AsObject();
                document["embedding"] = new JsonArray([.. stored.Embedding.Select(value => (JsonNode)value)]);

                using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
                using var response = await container.UpsertItemStreamAsync(content, new PartitionKey(version), cancellationToken: token);
                response.EnsureSuccessStatusCode();
            });
    }

    public async Task<int> CountAsync(string version, CancellationToken cancellationToken = default)
    {
        var count = 0;
        await foreach (var item in this.QueryAsync("SELECT VALUE COUNT(1) FROM c", version, cancellationToken))
        {
            count += item.GetValue<int>();
        }

        return count;
    }

    public async Task SetActiveVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        var document = new JsonObject
        {
            ["id"] = ActiveDocument,
            ["corpus_version"] = StatePartition,
            ["active_version"] = version,
            ["updated_at"] = DateTimeOffset.UtcNow,
        };

        using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        using var response = await container.UpsertItemStreamAsync(content, new PartitionKey(StatePartition), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = new List<string>();
        await foreach (var item in this.QueryAsync("SELECT DISTINCT VALUE c.corpus_version FROM c", null, cancellationToken))
        {
            if (item.GetValue<string>() is { } version && version != StatePartition)
            {
                versions.Add(version);
            }
        }

        return versions;
    }

    public async Task DeleteVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (var item in this.QueryAsync("SELECT VALUE c.id FROM c", version, cancellationToken))
        {
            ids.Add(item.GetValue<string>());
        }

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (id, token) =>
            {
                using var response = await container.DeleteItemStreamAsync(id, new PartitionKey(version), cancellationToken: token);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                }
            });
    }

    private async IAsyncEnumerable<JsonNode> QueryAsync(
        string query, string? partition, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = partition is null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(partition) };
        using var iterator = container.GetItemQueryStreamIterator(new QueryDefinition(query), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            using var page = await iterator.ReadNextAsync(cancellationToken);
            page.EnsureSuccessStatusCode();
            var body = await JsonNode.ParseAsync(page.Content, cancellationToken: cancellationToken);
            foreach (var item in body?["Documents"]?.AsArray() ?? [])
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }
}

/// <summary>The embedding deployment, through Entra ID, since key auth is off on the resource.</summary>
public sealed class AzureEmbedder(EmbeddingClient client) : IEmbedder
{
    private const string Scope = "https://ai.azure.com/.default";

    public static AzureEmbedder Create(Uri endpoint, string deployment, TokenCredential credential)
    {
        // The SDK retries 429s with backoff, honouring Retry-After, which the deployment's tokens-per-minute limit needs.
        var options = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromMinutes(2) };
#pragma warning disable OPENAI001 // The authentication policy constructor is experimental.
        var client = new OpenAIClient(new BearerTokenPolicy(credential, Scope), options);
#pragma warning restore OPENAI001
        return new AzureEmbedder(client.GetEmbeddingClient(deployment));
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var result = await client.GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken);
        return [.. result.Value.OrderBy(embedding => embedding.Index).Select(embedding => embedding.ToFloats().ToArray())];
    }
}

/// <summary>The identity the indexing job runs as, through workload identity from the workflow's token file.</summary>
public static class IndexCredential
{
    public static TokenCredential Create() => new DefaultAzureCredential();
}
