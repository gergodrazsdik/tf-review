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
        var app = WebApplication.CreateBuilder().Build();
        var options = new ApiOptions(List("TF_REVIEW_ALLOWED_REPOSITORY_IDS"), List("TF_REVIEW_RELEASED_COMMITS"));
        app.MapReviews(options, GitHubTokens.ForGitHub(ApiOptions.Audience), ServiceStores.FromEnvironment());
        await app.RunAsync();
    }

    public static void MapReviews(this WebApplication app, ApiOptions options, GitHubTokens tokens, ServiceStores stores)
    {
        var logger = app.Logger;
        var limiter = PartitionedRateLimiter.Create<string, string>(repository => RateLimitPartition.GetFixedWindowLimiter(
            repository,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ReviewsPerHour, Window = TimeSpan.FromHours(1) }));

        app.MapGet("/healthz", () => Results.Ok());

        app.MapPost("/reviews", async (HttpContext context) =>
        {
            var decision = await AuthenticateAsync(context, options, tokens, logger);
            if (decision.Failure is { } failure)
            {
                return failure;
            }

            var caller = decision.Caller!;
            using var lease = limiter.AttemptAcquire(caller.RepositoryId);
            if (!lease.IsAcquired)
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
            var decision = await AuthenticateAsync(context, options, tokens, logger);
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

    private static HashSet<string> List(string name) =>
        [.. (Environment.GetEnvironmentVariable(name) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The same inputs give the same ID, so a retry after a cold start is one review, and a re-run is another.</summary>
    internal static string ReviewId(Caller caller, string commit)
    {
        var key = $"{caller.RepositoryId}/{caller.PullRequest}/{commit}/{caller.RunId}/{caller.RunAttempt}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    private static async Task<(Caller? Caller, IResult? Failure)> AuthenticateAsync(
        HttpContext context, ApiOptions options, GitHubTokens tokens, ILogger logger)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return (null, Results.Unauthorized());
        }

        if (await tokens.ValidateAsync(header["Bearer ".Length..]) is not { } claims)
        {
            logger.LogInformation("Refused a token that isn't valid.");
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
            return request is not null && CommitFormat().IsMatch(request.Commit) && IsConfigPath(request.ConfigPath) ? request : null;
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
                    new ReviewJob(id, caller.RepositoryId, caller.Repository, caller.PullRequest, request.Commit, request.ConfigPath),
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
        [property: JsonPropertyName("plan")] string Plan);

    private sealed record ReviewResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("commit")] string Commit,
        [property: JsonPropertyName("pull_request")] int PullRequest,
        [property: JsonPropertyName("verdict")] string? Verdict);
}
