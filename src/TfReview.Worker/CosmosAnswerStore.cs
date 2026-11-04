using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using TfReview.Agents;

namespace TfReview.Worker;

/// <summary>
/// The result cache's answers (D53), in the reviews container beside each repo's review statuses: its partition key is the
/// repo's ID, which scopes the cache, and its free throughput is already there. Each answer expires after a week, like the
/// plans in blob storage, because an answer can quote what a plan holds.
/// </summary>
public sealed class CosmosAnswerStore(Container container) : IAnswerStore
{
    /// <summary>Seconds an answer lives. The container's default TTL is off (-1), so statuses never expire.</summary>
    public const int TimeToLive = 7 * 24 * 60 * 60;

    // Review IDs are 32 hex digits and keys 64, but a prefix keeps the two kinds apart whatever either becomes.
    private const string Prefix = "answer-";

    public async Task<string?> ReadAsync(string scope, string key, CancellationToken cancellationToken)
    {
        using var response = await container.ReadItemStreamAsync(Prefix + key, new PartitionKey(scope), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var document = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
        return document?["kind"]?.GetValue<string>() == "answer" ? document["answer"]?.GetValue<string>() : null;
    }

    public async Task WriteAsync(string scope, string key, string role, string answer, CancellationToken cancellationToken)
    {
        var document = new JsonObject
        {
            ["id"] = Prefix + key,
            ["repository"] = scope,
            ["kind"] = "answer",
            ["role"] = role,
            ["answer"] = answer,
            ["ttl"] = TimeToLive,
        };

        using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        using var response = await container.UpsertItemStreamAsync(content, new PartitionKey(scope), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
