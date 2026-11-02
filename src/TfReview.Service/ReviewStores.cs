namespace TfReview.Service;

/// <summary>What the worker has to do, and the only thing on the queue. The plan stays in blob storage.</summary>
public sealed record ReviewJob(
    string ReviewId,
    string RepositoryId,
    string Repository,
    int PullRequest,
    string Commit,
    string ConfigPath);

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

public interface IReviewQueue
{
    Task EnqueueAsync(ReviewJob job, CancellationToken cancellationToken = default);
}

public interface IReviewStatusStore
{
    /// <summary>Looks in one repo's partition only, so a caller can't read another repo's review by its ID.</summary>
    Task<ReviewStatus?> ReadAsync(string repositoryId, string reviewId, CancellationToken cancellationToken = default);

    Task WriteAsync(ReviewStatus status, CancellationToken cancellationToken = default);
}
