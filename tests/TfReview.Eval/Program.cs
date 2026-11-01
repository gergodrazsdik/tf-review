using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TfReview.Agents;
using TfReview.Core;
using TfReview.GitHub;
using TfReview.Rag;
using TfReview.Scanners;

// Runs the seeded cases through the real review. `replay` uses recorded model answers, offline and free,
// so it proves the code around the model and nothing about the model. `live` calls the real model, and is
// what the gate counts: every run of every case has to pass.
const string CasesDirectory = "tests/fixtures/cases";
const string Commit = "0000000000000000000000000000000000000000";
const string Config = $"""
    version: 1
    standards:
      repository: gergodrazsdik/tf-standards
      commit: {Commit}
    """;

JsonSerializerOptions fileOptions = new() { WriteIndented = true };

var cases = Directory.GetDirectories(CasesDirectory).Order(StringComparer.Ordinal).ToArray();
var standardsDirectory = Environment.GetEnvironmentVariable("TF_STANDARDS_DIR")
    ?? throw new InvalidOperationException("TF_STANDARDS_DIR isn't set.");

return args switch
{
    ["replay"] => await Replay(),
    ["live", "--runs", var runs] => await Live(int.Parse(runs, CultureInfo.InvariantCulture), null),
    ["live", "--runs", var runs, "--record", var directory] => await Live(int.Parse(runs, CultureInfo.InvariantCulture), directory),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: eval replay\n       eval live --runs <n> [--record <dir>]");
    return 2;
}

async Task<int> Replay()
{
    var version = Prompts.Load(ConventionsAgent.Name).Version;
    var failures = 0;

    foreach (var directory in cases)
    {
        var path = Path.Combine(directory, "answer.json");
        if (!File.Exists(path))
        {
            failures += Report(Path.GetFileName(directory), 1, ["no recorded answer"]);
            continue;
        }

        var answer = JsonSerializer.Deserialize<RecordedAnswer>(await File.ReadAllTextAsync(path))!;
        if (answer.PromptVersion != version)
        {
            failures += Report(Path.GetFileName(directory), 1, [$"recorded with prompt v{answer.PromptVersion}, and the prompt is v{version}: re-record it with the live run"]);
            continue;
        }

        var result = await Review(directory, new ReplayChat(answer.Text));
        failures += Report(Path.GetFileName(directory), 1, await Problems(directory, result));
    }

    Console.WriteLine(failures == 0 ? $"PASS: {cases.Length} case(s) replayed." : $"FAIL: {failures} case(s).");
    return failures == 0 ? 0 : 1;
}

async Task<int> Live(int runs, string? recordDirectory)
{
    var endpoint = new Uri(Required("TF_REVIEW_OPENAI_ENDPOINT"));
    var deployment = Required("TF_REVIEW_OPENAI_DEPLOYMENT");
    var version = Prompts.Load(ConventionsAgent.Name).Version;
    using var client = LlmClient.Create(endpoint, deployment);

    var failures = 0;
    var totalCost = 0m;
    foreach (var directory in cases)
    {
        var name = Path.GetFileName(directory);
        var passes = 0;
        for (var run = 1; run <= runs; run++)
        {
            using var recording = new RecordingChat(client);
            var result = await Review(directory, recording, deployment);
            var problems = await Problems(directory, result);
            failures += Report(name, run, problems);
            totalCost += result.Usage is { } usage ? StepSummary.EstimateCost(usage) ?? 0 : 0;

            if (result.EvidenceDrops.Count > 0)
            {
                var drops = string.Join(", ", result.EvidenceDrops.Select(drop => $"{drop.Key}: {drop.Value}"));
                Console.WriteLine($"  {name} run {run}: the evidence check dropped {drops}");
            }

            if (problems.Count == 0)
            {
                passes++;
                if (recordDirectory is not null && recording.Text is { } text)
                {
                    var target = Path.Combine(recordDirectory, name);
                    Directory.CreateDirectory(target);
                    await File.WriteAllTextAsync(
                        Path.Combine(target, "answer.json"),
                        JsonSerializer.Serialize(new RecordedAnswer(version, text), fileOptions) + "\n");
                }
            }
        }

        Console.WriteLine($"{name}: {passes} of {runs}");
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Prompt v{version}, model {deployment}, estimated cost ~${totalCost:0.0000}."));
    Console.WriteLine(failures == 0 ? $"PASS: {cases.Length} case(s), {runs} run(s) each." : $"FAIL: {failures} failed run(s).");
    return failures == 0 ? 0 : 1;
}

async Task<ReviewResult> Review(string directory, IChatClient chat, string deployment = "replay")
{
    var runner = new ReviewRunner(
        new LocalDirectorySource(standardsDirectory),
        new ConftestRunner(new ProcessRunner()),
        new ReviewModel(chat, deployment));

    return await runner.RunAsync(await File.ReadAllTextAsync(Path.Combine(directory, "plan.json")), Config, Commit);
}

// The whole set has to match: an extra finding is a false positive, and it fails the case like a missed one.
async Task<List<string>> Problems(string directory, ReviewResult result)
{
    var expected = JsonSerializer.Deserialize<ExpectedCase>(await File.ReadAllTextAsync(Path.Combine(directory, "expected.json")))!;
    var problems = new List<string>();

    foreach (var missing in result.NotReviewed)
    {
        problems.Add($"not reviewed: {missing.Scope} ({missing.Reason})");
    }

    var actual = result.Findings
        .Select(finding => new ExpectedFinding(finding.ResourceAddress, finding.Rule, Name(finding.Agent), finding.Deterministic))
        .ToList();

    problems.AddRange(expected.Findings.Except(actual).Select(finding => $"missing: {finding}"));
    problems.AddRange(actual.Except(expected.Findings).Select(finding => $"unexpected: {finding}"));

    foreach (var finding in result.Findings)
    {
        var pointer = finding.Evidence.Any(item => item.Value is PlanEvidence);
        var citation = finding.Evidence.Any(item => item.Value is DocEvidence doc && doc.DocId == finding.Rule);
        if (!pointer || !citation)
        {
            problems.Add($"{finding.ResourceAddress} {finding.Rule}: no plan pointer or citation");
        }
    }

    return problems;
}

static int Report(string name, int run, IReadOnlyList<string> problems)
{
    foreach (var problem in problems)
    {
        Console.WriteLine($"FAILED {name} run {run}: {problem}");
    }

    return problems.Count == 0 ? 0 : 1;
}

static string Name(Reporter reporter) => JsonSerializer.Serialize(reporter).Trim('"');

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} isn't set.");

internal sealed record ExpectedCase([property: JsonPropertyName("findings")] IReadOnlyList<ExpectedFinding> Findings);

internal sealed record ExpectedFinding(
    [property: JsonPropertyName("resource_address")] string ResourceAddress,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("deterministic")] bool Deterministic);

internal sealed record RecordedAnswer(
    [property: JsonPropertyName("prompt_version")] int PromptVersion,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Always gives the same answer, for runs that mustn't reach the model.</summary>
internal sealed class ReplayChat(string text) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Keeps the model's last answer, so a passing run can become a replay.</summary>
internal sealed class RecordingChat(IChatClient inner) : DelegatingChatClient(inner)
{
    public string? Text { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Text = response.Text;
        return response;
    }

    // The client is shared across runs, so disposing one run's wrapper mustn't dispose it.
    protected override void Dispose(bool disposing)
    {
    }
}
