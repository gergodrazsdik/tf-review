using System.Text.Json;
using System.Text.RegularExpressions;
using TfReview.Agents;
using TfReview.Core;
using TfReview.GitHub;
using TfReview.Rag;
using TfReview.Scanners;

const string Usage = """
    usage: tfreview redact <plan.json> <redacted.json>
           tfreview review <redacted.json> <config.yml> <result.json> <commit>
           tfreview publish <result.json> <redacted.json> <commit>
    """;

switch (args)
{
    case ["redact", var plan, var output]:
        return await Redact(plan, output);
    case ["review", var redacted, var config, var output, var commit] when IsCommit(commit):
        return await Review(redacted, config, output, commit);
    case ["publish", var result, var redacted, var commit] when IsCommit(commit):
        return await Publish(result, redacted, commit);
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

// A refused plan writes a marker instead, which the review refuses in turn, so it reports the reason.
static async Task<int> Redact(string planPath, string outputPath)
{
    var outcome = new PlanRedactor().Redact(await File.ReadAllTextAsync(planPath));
    switch (outcome)
    {
        case Redacted redacted:
            await File.WriteAllTextAsync(outputPath, redacted.Plan.ToCanonicalJson());
            Console.WriteLine($"Redacted {redacted.SecretsFound} value(s), and dropped {redacted.FieldsDropped} field(s).");
            break;
        case Refused refused:
            await File.WriteAllTextAsync(outputPath, """{"refused":true}""");
            Console.WriteLine($"Refused the plan: {refused.Rule} at {refused.Location}.");
            break;
    }

    return 0;
}

// Any failure becomes a result, so the review job doesn't need to work out what went wrong.
static async Task<int> Review(string redactedPath, string configPath, string outputPath, string commit)
{
    var redacted = await ReadOrNull(redactedPath);
    var config = await ReadOrNull(configPath);

    ReviewResult result;
    if (redacted is null)
    {
        result = ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.Unavailable);
    }
    else
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://codeload.github.com/") };
        var runner = new ReviewRunner(new GitHubTarballSource(http), new ConftestRunner(new ProcessRunner()));
        result = await runner.RunAsync(redacted, config, commit);
    }

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result));
    Console.WriteLine($"Verdict {result.Verdict} with {result.Findings.Count} finding(s).");
    return 0;
}

// Always reports something: a failed review must not leave an older result standing on the PR.
static async Task<int> Publish(string resultPath, string redactedPath, string commit)
{
    ReviewResult? result = null;
    if (await ReadOrNull(resultPath) is { } json)
    {
        try
        {
            result = ReviewResult.Parse(json);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("Ignoring a malformed result.");
        }
    }

    if (result?.Commit != commit)
    {
        result = ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.Unavailable);
    }

    PlanCounts? counts = null;
    if (await ReadOrNull(redactedPath) is { } plan && new PlanRedactor().Redact(plan) is Redacted redacted)
    {
        counts = PlanCounts.From(redacted.Plan);
    }

    var body = CommentRenderer.Render(result, counts);

    var repository = Required("GITHUB_REPOSITORY");
    var pullRequest = int.Parse(Required("PR_NUMBER"));
    var apiUrl = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";

    using var http = StickyComment.CreateClient(new Uri(apiUrl.TrimEnd('/') + "/"), Required("GITHUB_TOKEN"));
    var action = await new StickyComment(http, repository, pullRequest).UpsertAsync(body);
    Console.WriteLine($"Comment {action.ToString().ToLowerInvariant()} for commit {commit}.");
    return 0;
}

// A missing file is expected when an earlier job failed.
static async Task<string?> ReadOrNull(string path)
{
    try
    {
        return await File.ReadAllTextAsync(path);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Couldn't read {Path.GetFileName(path)}: {e.GetType().Name}.");
        return null;
    }
}

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} isn't set.");

static bool IsCommit(string value) => Regex.IsMatch(value, "^[0-9a-f]{40}$");
