using System.Text.Json;
using System.Text.RegularExpressions;
using TfReview.Core;
using TfReview.GitHub;

const string Usage = """
    usage: tfreview redact <plan.json> <counts.json>
           tfreview review <counts.json> <result.json> <commit>
           tfreview publish <result.json> <counts.json> <commit>
    """;

switch (args)
{
    case ["redact", var plan, var output]:
        return await Redact(plan, output);
    case ["review", var counts, var output, var commit] when IsCommit(commit):
        return await Review(counts, output, commit);
    case ["publish", var result, var counts, var commit] when IsCommit(commit):
        return await Publish(result, counts, commit);
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

// Stub: writes only counts, so no plan content leaves the plan job until real redaction exists.
static async Task<int> Redact(string planPath, string outputPath)
{
    await using var input = File.OpenRead(planPath);
    using var plan = await JsonDocument.ParseAsync(input);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(PlanCounts.FromPlan(plan)));
    return 0;
}

// Stub: there's no agent yet, so every review reports that it didn't happen.
static async Task<int> Review(string countsPath, string outputPath, string commit)
{
    PlanCounts.Parse(await File.ReadAllTextAsync(countsPath));
    var result = ReviewResult.Failed(commit, ReviewScope.Conventions, NotReviewedReason.Unavailable);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result));
    return 0;
}

// Always reports something: a failed review must not leave an older result standing on the PR.
static async Task<int> Publish(string resultPath, string countsPath, string commit)
{
    var result = await ReadOrNull(resultPath, ReviewResult.Parse);
    if (result?.Commit != commit)
    {
        result = ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.Unavailable);
    }

    var counts = await ReadOrNull(countsPath, PlanCounts.Parse);
    var body = CommentRenderer.Render(result, counts);

    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
    {
        await File.AppendAllTextAsync(summary, body);
    }

    var repository = Required("GITHUB_REPOSITORY");
    var pullRequest = int.Parse(Required("PR_NUMBER"));
    var apiUrl = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";

    using var http = StickyComment.CreateClient(new Uri(apiUrl.TrimEnd('/') + "/"), Required("GITHUB_TOKEN"));
    var action = await new StickyComment(http, repository, pullRequest).UpsertAsync(body);
    Console.WriteLine($"Comment {action.ToString().ToLowerInvariant()} for commit {commit}.");
    return 0;
}

// Missing or malformed files are expected when an earlier job failed or was tampered with.
static async Task<T?> ReadOrNull<T>(string path, Func<string, T?> parse) where T : class
{
    try
    {
        return parse(await File.ReadAllTextAsync(path));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
    {
        Console.Error.WriteLine($"Ignoring {path}: {e.GetType().Name}.");
        return null;
    }
}

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} isn't set.");

static bool IsCommit(string value) => Regex.IsMatch(value, "^[0-9a-f]{40}$");
