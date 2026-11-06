using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TfReview.Core;
using TfReview.Service;

namespace TfReview.Api;

/// <summary>What the API allows, from its environment in production.</summary>
public sealed record ApiOptions(IReadOnlySet<string> AllowedRepositoryIds, IReadOnlySet<string> ReleasedCommits)
{
    public const string Audience = "tf-review-api";

    /// <summary>Well above any plan this reviews, and low enough that a caller can't fill the small replica's memory.</summary>
    public long MaxRequestBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Per repo. A token is needed to get this far, so it only stops one repo spending everyone's budget.</summary>
    public int ReviewsPerHour { get; init; } = 60;
}

/// <summary>Takes reviews from the workflow's review job and leaves the work to the queue. Ingress is public.</summary>
public static partial class ReviewApi
{
    private const string RefusedPlan = """{"refused":true}""";

    /// <summary>How often refused tokens are counted in the log, rather than a line each (SEC-7).</summary>
    private static readonly TimeSpan RefusalLogPeriod = TimeSpan.FromMinutes(5);

    /// <summary>An hour's count outlives its hour, then expires by itself.</summary>
    private static readonly TimeSpan RateWindowLife = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions ResponseOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Runs the service's API from its environment. An empty list refuses everyone.</summary>
    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Each request's framework lines would use up the daily log cap, and these say nothing the app's own don't.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var app = builder.Build();
        var options = new ApiOptions(List("TF_REVIEW_ALLOWED_REPOSITORY_IDS"), List("TF_REVIEW_RELEASED_COMMITS"));
        app.MapReviews(options, GitHubTokens.ForGitHub(ApiOptions.Audience), ServiceStores.FromEnvironment());
        await app.RunAsync();
    }

    public static void MapReviews(this WebApplication app, ApiOptions options, GitHubTokens tokens, ServiceStores stores)
    {
        var logger = app.Logger;
        var refusals = new RefusalLog(
            count => logger.LogInformation("Refused {Count} token(s) that aren't valid since the last count.", count), RefusalLogPeriod);
        var limiter = PartitionedRateLimiter.Create<string, string>(repository => RateLimitPartition.GetFixedWindowLimiter(
            repository,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ReviewsPerHour, Window = TimeSpan.FromHours(1) }));

        app.MapGet("/healthz", () => Results.Ok());

        app.MapPost("/reviews", async (HttpContext context) =>
        {
            var decision = await AuthenticateAsync(context, options, tokens, logger, refusals);
            if (decision.Failure is { } failure)
            {
                return failure;
            }

            var caller = decision.Caller!;
            if (!await WithinRateAsync(caller, options, stores, limiter, logger, context.RequestAborted))
            {
                logger.LogWarning("Rate limited repository {Repository}.", caller.RepositoryId);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var body = await ReadBodyAsync(context, options.MaxRequestBytes);
            if (body.TooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (Parse(body.Bytes!) is not { } request)
            {
                return Results.BadRequest();
            }

            return await SubmitAsync(caller, request, stores, logger, context.RequestAborted);
        });

        app.MapGet("/reviews/{id}", async (string id, HttpContext context) =>
        {
            var decision = await AuthenticateAsync(context, options, tokens, logger, refusals);
            if (decision.Failure is { } failure)
            {
                return failure;
            }

            if (!ReviewIdFormat().IsMatch(id))
            {
                return Results.NotFound();
            }

            try
            {
                return await stores.Statuses.ReadAsync(decision.Caller!.RepositoryId, id, context.RequestAborted) is { } status
                    ? Results.Json(ToResponse(status), ResponseOptions)
                    : Results.NotFound();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError("Couldn't read a review: {Type}.", e.GetType().Name);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    /// <summary>
    /// The per-repo limit, counted in the store by repo and hour, so a replica scaling to zero doesn't reset it (SEC-5).
    /// If the store fails, this replica's own count stands in, which is what it was before.
    /// </summary>
    private static async Task<bool> WithinRateAsync(
        Caller caller,
        ApiOptions options,
        ServiceStores stores,
        PartitionedRateLimiter<string> limiter,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (stores.Counters is { } counters)
        {
            try
            {
                var hour = DateTimeOffset.UtcNow.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
                return await counters.AddAsync(caller.RepositoryId, $"rate-{hour}", 1, RateWindowLife, cancellationToken) <= options.ReviewsPerHour;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't count a review against the rate limit, so this replica counts it: {Type}.", e.GetType().Name);
            }
        }

        using var lease = limiter.AttemptAcquire(caller.RepositoryId);
        return lease.IsAcquired;
    }

    private static HashSet<string> List(string name) =>
        [.. (Environment.GetEnvironmentVariable(name) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The same inputs give the same ID, so a retry after a cold start is one review, and a re-run is another.</summary>
    internal static string ReviewId(Caller caller, string commit)
    {
        var key = $"{caller.RepositoryId}/{caller.PullRequest}/{commit}/{caller.RunId}/{caller.RunAttempt}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    private static async Task<(Caller? Caller, IResult? Failure)> AuthenticateAsync(
        HttpContext context, ApiOptions options, GitHubTokens tokens, ILogger logger, RefusalLog refusals)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return (null, Results.Unauthorized());
        }

        if (await tokens.ValidateAsync(header["Bearer ".Length..]) is not { } claims)
        {
            refusals.Refused();
            return (null, Results.Unauthorized());
        }

        var decision = CallerPolicy.Check(claims, options);
        if (decision.Caller is { } caller)
        {
            return (caller, null);
        }

        // These rules are in a public repo, so naming the one that failed tells a caller nothing new.
        logger.LogWarning("Refused a caller: {Refusal}.", decision.Refusal);
        return (null, Results.Json(new { refused = decision.Refusal.ToString() }, statusCode: StatusCodes.Status403Forbidden));
    }

    private static async Task<(byte[]? Bytes, bool TooLarge)> ReadBodyAsync(HttpContext context, long limit)
    {
        if (context.Request.ContentLength > limit)
        {
            return (null, true);
        }

        // A declared length can be missing, so the count below is what really limits it.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (null, true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    private static SubmitRequest? Parse(byte[] body)
    {
        try
        {
            var request = JsonSerializer.Deserialize<SubmitRequest>(body, RequestOptions);
            return request is not null
                && CommitFormat().IsMatch(request.Commit)
                && IsConfigPath(request.ConfigPath)
                && (request.WorkingDirectory is null || IsConfigPath(request.WorkingDirectory))
                ? request
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // It's only used to read a file through GitHub's API, but it's still text from a caller.
    private static bool IsConfigPath(string path) =>
        ConfigPathFormat().IsMatch(path) && !path.Split('/').Contains("..", StringComparer.Ordinal);

    private static async Task<IResult> SubmitAsync(
        Caller caller, SubmitRequest request, ServiceStores stores, ILogger logger, CancellationToken cancellationToken)
    {
        var id = ReviewId(caller, request.Commit);
        try
        {
            // Queued last, so a retry after any failure here gets the whole way, and a duplicate message only
            // costs the worker a look at the status.
            if (await stores.Statuses.ReadAsync(caller.RepositoryId, id, cancellationToken) is not { } status)
            {
                await stores.Plans.SaveAsync(id, RedactAgain(request.Plan, logger), cancellationToken);
                await stores.Queue.EnqueueAsync(
                    new ReviewJob(id, caller.RepositoryId, caller.Repository, caller.PullRequest, request.Commit, request.ConfigPath, request.WorkingDirectory),
                    cancellationToken);

                status = new ReviewStatus(
                    id, caller.RepositoryId, caller.Repository, caller.PullRequest, request.Commit, ReviewState.Queued, DateTimeOffset.UtcNow);
                await stores.Statuses.WriteAsync(status, cancellationToken);
            }

            return Results.Json(ToResponse(status), ResponseOptions, statusCode: StatusCodes.Status202Accepted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Only the type, as a message could quote the request. The workflow retries, and then says "not reviewed".
            logger.LogError("Couldn't accept a review: {Type}.", e.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // The plan comes from a job that ran PR code. A refused one still goes through, so the PR gets told why.
    private static string RedactAgain(string plan, ILogger logger)
    {
        switch (new PlanRedactor().Redact(plan))
        {
            case Redacted redacted:
                return redacted.Plan.ToCanonicalJson();
            case Refused refused:
                logger.LogWarning("Refused a plan: {Rule} at {Location}.", refused.Rule, refused.Location);
                return RefusedPlan;
            default:
                return RefusedPlan;
        }
    }

    private static ReviewResponse ToResponse(ReviewStatus status) =>
        new(status.ReviewId, status.State.ToString().ToLowerInvariant(), status.Commit, status.PullRequest, status.Verdict);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitFormat();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ReviewIdFormat();

    [GeneratedRegex("^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$")]
    private static partial Regex ConfigPathFormat();

    private sealed record SubmitRequest(
        [property: JsonPropertyName("commit")] string Commit,
        [property: JsonPropertyName("config_path")] string ConfigPath,
        [property: JsonPropertyName("plan")] string Plan,

        // Where the project is, to read its lock file at the base commit. Callers from before it was sent leave it out.
        [property: JsonPropertyName("working_directory")] string? WorkingDirectory = null);

    private sealed record ReviewResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("commit")] string Commit,
        [property: JsonPropertyName("pull_request")] int PullRequest,
        [property: JsonPropertyName("verdict")] string? Verdict);
}
