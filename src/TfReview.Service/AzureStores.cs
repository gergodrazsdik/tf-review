using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;

namespace TfReview.Service;

/// <summary>Where the service keeps things, from its environment. Everything authenticates with its identity.</summary>
public sealed record ServiceStores(IPlanStore Plans, IReviewQueue Queue, IReviewStatusStore Statuses)
{
    private const string PlansContainer = "plans";
    private const string QueueName = "reviews";
    private const string Database = "tfreview";
    private const string ReviewsContainer = "reviews";

    /// <summary>Durable counters, in the reviews container beside the statuses, or null for none (counted in memory).</summary>
    public ICounterStore? Counters { get; init; }

    public static ServiceStores FromEnvironment()
    {
        var credential = new DefaultAzureCredential();
        var account = Required("TF_REVIEW_STORAGE_ACCOUNT");

        // Stream APIs below, so the Cosmos SDK's own JSON serializer never sees a document.
        var cosmos = new CosmosClient(
            Required("TF_REVIEW_COSMOS_ENDPOINT"),
            credential,
            new CosmosClientOptions { ApplicationName = "tf-review", ConnectionMode = ConnectionMode.Gateway });

        var reviews = cosmos.GetContainer(Database, ReviewsContainer);
        return new ServiceStores(
            new BlobPlanStore(new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/{PlansContainer}"), credential)),
            new StorageReviewQueue(new QueueClient(new Uri($"https://{account}.queue.core.windows.net/{QueueName}"), credential)),
            new CosmosStatusStore(reviews))
        {
            Counters = new CosmosCounterStore(reviews),
        };
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} isn't set.");
}

internal sealed class BlobPlanStore(BlobContainerClient container) : IPlanStore
{
    public async Task SaveAsync(string reviewId, string plan, CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(plan));
        await container.GetBlobClient(Name(reviewId)).UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async Task<string?> ReadAsync(string reviewId, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await container.GetBlobClient(Name(reviewId)).DownloadContentAsync(cancellationToken);
            return content.Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string Name(string reviewId) => $"{reviewId}.json";
}

internal sealed class StorageReviewQueue(QueueClient queue) : IReviewQueue
{
    public async Task EnqueueAsync(ReviewJob job, CancellationToken cancellationToken = default) =>
        await queue.SendMessageAsync(JsonSerializer.Serialize(job), cancellationToken);

    public async Task<ReceivedJob?> ReceiveAsync(TimeSpan visibility, CancellationToken cancellationToken = default)
    {
        var response = await queue.ReceiveMessageAsync(visibility, cancellationToken);
        if (response.Value is not { } message)
        {
            return null;
        }

        ReviewJob? job;
        try
        {
            job = JsonSerializer.Deserialize<ReviewJob>(message.MessageText);
        }
        catch (JsonException)
        {
            job = null;
        }

        return new ReceivedJob(
            job,
            message.DequeueCount,
            async token => await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, token));
    }
}

internal sealed class CosmosStatusStore(Container container) : IReviewStatusStore
{
    public async Task<ReviewStatus?> ReadAsync(string repositoryId, string reviewId, CancellationToken cancellationToken = default)
    {
        using var response = await container.ReadItemStreamAsync(reviewId, new PartitionKey(repositoryId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var document = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken)
            ?? throw new JsonException("Cosmos returned an empty review.");

        return new ReviewStatus(
            document["id"]!.GetValue<string>(),
            document["repository"]!.GetValue<string>(),
            document["repository_name"]!.GetValue<string>(),
            document["pull_request"]!.GetValue<int>(),
            document["commit"]!.GetValue<string>(),
            Enum.Parse<ReviewState>(document["state"]!.GetValue<string>()),
            document["updated_at"]!.GetValue<DateTimeOffset>())
        {
            Verdict = document["verdict"]?.GetValue<string>(),
        };
    }

    public async Task WriteAsync(ReviewStatus status, CancellationToken cancellationToken = default)
    {
        // The container's partition key is /repository, and it holds the ID, which survives a rename.
        var document = new JsonObject
        {
            ["id"] = status.ReviewId,
            ["repository"] = status.RepositoryId,
            ["repository_name"] = status.Repository,
            ["pull_request"] = status.PullRequest,
            ["commit"] = status.Commit,
            ["state"] = status.State.ToString(),
            ["updated_at"] = status.UpdatedAt,
            ["verdict"] = status.Verdict,
        };

        using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        using var response = await container.UpsertItemStreamAsync(content, new PartitionKey(status.RepositoryId), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Counters as documents in the reviews container, each with its own TTL, which the container's default of -1 allows.
/// Two writers at once are kept apart by the document's ETag: the loser reads again and retries.
/// </summary>
internal sealed class CosmosCounterStore(Container container) : ICounterStore
{
    private const int MaxTries = 5;

    public async Task<double> ReadAsync(string partition, string id, CancellationToken cancellationToken = default) =>
        (await this.ReadDocumentAsync(partition, id, cancellationToken)).Value;

    public async Task<double> AddAsync(string partition, string id, double amount, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (value, etag) = await this.ReadDocumentAsync(partition, id, cancellationToken);
            var document = new JsonObject
            {
                ["id"] = id,
                ["repository"] = partition,
                ["kind"] = "counter",
                ["value"] = value + amount,
                ["ttl"] = (int)timeToLive.TotalSeconds,
            };

            using var content = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
            using var response = etag is null
                ? await container.CreateItemStreamAsync(content, new PartitionKey(partition), cancellationToken: cancellationToken)
                : await container.ReplaceItemStreamAsync(
                    content, id, new PartitionKey(partition), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed && attempt < MaxTries)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            return value + amount;
        }
    }

    private async Task<(double Value, string? ETag)> ReadDocumentAsync(string partition, string id, CancellationToken cancellationToken)
    {
        using var response = await container.ReadItemStreamAsync(id, new PartitionKey(partition), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (0, null);
        }

        response.EnsureSuccessStatusCode();
        var document = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
        return (document?["kind"]?.GetValue<string>() == "counter" ? document["value"]?.GetValue<double>() ?? 0 : 0, response.Headers.ETag);
    }
}
