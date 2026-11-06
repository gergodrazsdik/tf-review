namespace TfReview.Service;

/// <summary>What the worker has to do, and the only thing on the queue. The plan stays in blob storage.</summary>
public sealed record ReviewJob(
    string ReviewId,
    string RepositoryId,
    string Repository,
    int PullRequest,
    string Commit,
    string ConfigPath,
    string? WorkingDirectory = null);

public enum ReviewState
{
    Queued,
    Running,
    Done,
    Failed,
    Superseded,
}

/// <summary>One review's progress. It holds no plan values, so it's safe to answer a caller with.</summary>
public sealed record ReviewStatus(
    string ReviewId,
    string RepositoryId,
    string Repository,
    int PullRequest,
    string Commit,
    ReviewState State,
    DateTimeOffset UpdatedAt)
{
    public string? Verdict { get; init; }
}

public interface IPlanStore
{
    Task SaveAsync(string reviewId, string plan, CancellationToken cancellationToken = default);

    Task<string?> ReadAsync(string reviewId, CancellationToken cancellationToken = default);
}

/// <summary>A message taken off the queue, which comes back unless it's completed. A job of null couldn't be read.</summary>
public sealed record ReceivedJob(ReviewJob? Job, long DequeueCount, Func<CancellationToken, Task> CompleteAsync);

public interface IReviewQueue
{
    Task EnqueueAsync(ReviewJob job, CancellationToken cancellationToken = default);

    /// <summary>The next message, hidden from others for this long, or null if the queue is empty.</summary>
    Task<ReceivedJob?> ReceiveAsync(TimeSpan visibility, CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts that outlive a replica or a job: the API's rate limit per repo and hour, and the worker's daily model spend.
/// Each counter is a document in a partition, and expires on its own.
/// </summary>
public interface ICounterStore
{
    /// <summary>The counter's value, or 0 when it doesn't exist.</summary>
    Task<double> ReadAsync(string partition, string id, CancellationToken cancellationToken = default);

    /// <summary>Adds to the counter, creating it with this lifetime, and returns the new value.</summary>
    Task<double> AddAsync(string partition, string id, double amount, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}

public interface IReviewStatusStore
{
    /// <summary>Looks in one repo's partition only, so a caller can't read another repo's review by its ID.</summary>
    Task<ReviewStatus?> ReadAsync(string repositoryId, string reviewId, CancellationToken cancellationToken = default);

    Task WriteAsync(ReviewStatus status, CancellationToken cancellationToken = default);
}
