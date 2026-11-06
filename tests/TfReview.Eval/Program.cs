using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TfReview.Agents;
using TfReview.Core;
using TfReview.Eval;
using TfReview.GitHub;
using TfReview.Rag;
using TfReview.Scanners;

// Runs the seeded cases through the real review. Conftest always runs for real. Checkov and Infracost replay
// what `record-scans` saved, because CI has neither Checkov nor the token, and prices shouldn't move a test.
// `replay` also uses recorded model answers, offline and free, so it proves the code around the model and
// nothing about the model. `live` calls the real model, and is what the gate counts.
const string CasesDirectory = "tests/fixtures/cases";
const string Commit = "0000000000000000000000000000000000000000";
const string Config = $"""
    version: 1
    standards:
      repository: gergodrazsdik/tf-standards
      commit: {Commit}
    agents: [conventions, security, cost]
    """;

string[] profiles = [AgentProfile.Conventions.Name, AgentProfile.Security.Name, AgentProfile.Cost.Name, RiskOrchestrator.Name, LlmArbiter.Name, ReferenceSuggester.Name];

// Relaxed escaping keeps recorded answers readable in a diff. These files are never rendered as HTML.
JsonSerializerOptions fileOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// Every result, replayed or live, has to match the contract other repos read.
var contracts = new TfReview.Eval.Contracts("docs/contracts");

// Only directories with a plan, so a stray one such as a tool's own can't fail the run.
var cases = Directory.GetDirectories(CasesDirectory)
    .Where(directory => File.Exists(Path.Combine(directory, "plan.json")))
    .Order(StringComparer.Ordinal)
    .ToArray();
// Rescoring saved runs reads no standards, so only the commands that review need it.
var standardsDirectory = args is ["rescore", ..] ? string.Empty : Environment.GetEnvironmentVariable("TF_STANDARDS_DIR")
    ?? throw new InvalidOperationException("TF_STANDARDS_DIR isn't set.");

return args switch
{
    ["replay"] => await Replay(),
    ["rescore", .. var saved] when saved.Length > 0 => Rescore(saved),
    ["record-scans", .. var names] => await RecordScans(names),
    ["live", "--runs", var runs, .. var options] => await Live(
        int.Parse(runs, CultureInfo.InvariantCulture), Option(options, "--record"), options.Contains("--references"), Option(options, "--suggestions"),
        Option(options, "--cases")?.Split(','),
        Option(options, "--placement") is "agent" ? ReferencePlacement.Agent : ReferencePlacement.Finding,
        options.Contains("--show-answers")),
    ["retrieval", var labels] => await TfReview.Eval.RetrievalEval.RunAsync(labels, CasesDirectory, standardsDirectory, null),
    ["retrieval", var labels, "--report", var report] => await TfReview.Eval.RetrievalEval.RunAsync(labels, CasesDirectory, standardsDirectory, report),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: eval replay\n       eval record-scans [case...]\n       eval live --runs <n> [--record <dir>] [--references] [--suggestions <dir>] [--cases <name,...>] [--placement agent|finding] [--show-answers]\n       eval rescore <suggestions dir>...\n       eval retrieval <labels.yml> [--report <file.md>]");
    return 2;
}

async Task<int> Replay()
{
    var failures = 0;
    foreach (var directory in cases)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var profile in profiles)
        {
            var path = Path.Combine(directory, "answers", $"{profile}.json");
            if (File.Exists(path))
            {
                var answer = JsonSerializer.Deserialize<RecordedAnswer>(await File.ReadAllTextAsync(path))!;
                var version = Prompts.Load(profile).Version;
                if (answer.PromptVersion != version)
                {
                    stale.Add($"{profile} was recorded with prompt v{answer.PromptVersion}, and the prompt is v{version}: re-record it with the live run");
                }

                answers[profile] = answer.Text;
            }
        }

        var chat = new ReplayChat(answers, profiles);
        var result = await Review(directory, chat);
        failures += Report(Path.GetFileName(directory), 1, [.. stale, .. chat.Missing, .. Problems(directory, result), .. contracts.Problems(result)]);

        // Production's default with references: per finding, through the suggester (D50).
        if (TfReview.Eval.ReplayReferences.Load(directory) is { } references)
        {
            var withChat = new ReplayChat(answers, profiles);
            var withReferences = await Review(directory, withChat, references: references);
            failures += Report(
                $"{Path.GetFileName(directory)} with references",
                1,
                [.. stale, .. withChat.Missing, .. Problems(directory, withReferences), .. contracts.Problems(withReferences), .. references.Problems(result, withReferences)]);
        }
    }

    Console.WriteLine(failures == 0 ? $"PASS: {cases.Length} case(s) replayed." : $"FAIL: {failures} replay(s).");
    return failures == 0 ? 0 : 1;
}

// Saves what the real scanners say about each named case's plan, or all of them if none is named. It needs
// Checkov on PATH and the Infracost token.
async Task<int> RecordScans(string[] names)
{
    foreach (var directory in cases.Where(directory => names.Length == 0 || names.Contains(Path.GetFileName(directory), StringComparer.Ordinal)))
    {
        foreach (var stale in Directory.GetFiles(directory, "*.scan"))
        {
            File.Delete(stale);
        }

        var scans = new RecordingScans(new ProcessRunner(InfracostRunner.EnvironmentVariables), directory);
        var plan = new PlanRedactor().Redact(await File.ReadAllTextAsync(Path.Combine(directory, "plan.json")));
        if (plan is not Redacted redacted)
        {
            throw new InvalidOperationException($"{directory}: the redactor refused the plan.");
        }

        var checkov = await new CheckovRunner(scans).RunAsync(redacted.Plan);
        var infracost = await new InfracostRunner(scans).RunAsync(redacted.Plan);
        Console.WriteLine($"{Path.GetFileName(directory)}: {Describe(checkov)}, {Describe(infracost)}");
        if (checkov is CheckovFailed || infracost is InfracostFailed)
        {
            return 1;
        }
    }

    return 0;
}

static string Describe(object outcome) => outcome switch
{
    CheckovRan ran => $"Checkov {ran.Violations.Count} on the allowlist",
    CheckovFailed failed => $"Checkov FAILED {failed.Reason}",
    InfracostRan ran => string.Create(CultureInfo.InvariantCulture, $"Infracost {ran.TotalDelta:+0.00;-0.00;0.00} a month"),
    InfracostFailed failed => $"Infracost FAILED {failed.Reason}",
    _ => "unknown",
};

static string? Option(string[] options, string name) =>
    Array.IndexOf(options, name) is var index and >= 0 && index + 1 < options.Length ? options[index + 1] : null;

// With --references the config turns reference material on, and agents read the real index with the lock file below.
// The baseline has to hold with it, and --suggestions saves what each run suggested, to read against a run without.
// Cases with a suggestions.json also score their suggestions, which is reported and never fails a run.
async Task<int> Live(int runs, string? recordDirectory, bool withReferences, string? suggestionsDirectory, string[]? only, ReferencePlacement placement, bool showAnswers)
{
    var selected = only is null ? cases : cases.Where(directory => only.Contains(Path.GetFileName(directory), StringComparer.Ordinal)).ToArray();
    if (only is not null && selected.Length != only.Length)
    {
        Console.Error.WriteLine($"No such case among: {string.Join(", ", only)}");
        return 2;
    }

    var endpoint = new Uri(Required("TF_REVIEW_OPENAI_ENDPOINT"));
    var deployment = Required("TF_REVIEW_OPENAI_DEPLOYMENT");
    using var client = LlmClient.Create(endpoint, deployment);
    var references = withReferences
        ? new TfReview.Service.CosmosReferenceRetriever(
            new Microsoft.Azure.Cosmos.CosmosClient(
                    Required("TF_REVIEW_COSMOS_ENDPOINT"),
                    new Azure.Identity.DefaultAzureCredential(),
                    new Microsoft.Azure.Cosmos.CosmosClientOptions { ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway })
                .GetContainer("tfreview", "references"),
            TfReview.Service.AzureQueryEmbedder.Create(endpoint, "text-embedding-3-small", new Azure.Identity.DefaultAzureCredential()))
        : null;

    var failures = 0;
    var totalCost = 0m;
    var inputTokens = 0L;
    var cachedTokens = 0L;
    var conceptsFound = 0;
    var conceptsAsked = 0;
    var tally = new Tally();
    foreach (var directory in selected)
    {
        var name = Path.GetFileName(directory);
        var passes = 0;
        var scoring = SuggestionScore.Load(directory);
        var caseFound = 0;
        var caseAsked = 0;
        for (var run = 1; run <= runs; run++)
        {
            using var recording = new RecordingChat(client, profiles);
            var result = await Review(directory, recording, deployment, references, placement);
            if (suggestionsDirectory is not null)
            {
                Directory.CreateDirectory(Path.Combine(suggestionsDirectory, name));
                await File.WriteAllTextAsync(
                    Path.Combine(suggestionsDirectory, name, $"run-{run}.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            findings = result.Findings.Select(finding => new { finding.ResourceAddress, finding.Rule, finding.Title, finding.Suggestion }),
                            references = result.References,
                        },
                        fileOptions) + "\n");
            }

            List<string> problems = [.. Problems(directory, result), .. contracts.Problems(result)];
            failures += Report(name, run, problems);

            // For working out why a model answered as it did. Evals only see fixtures, so nothing private reaches the log.
            if (showAnswers && problems.Count > 0)
            {
                foreach (var (agent, text) in recording.Answers.OrderBy(answer => answer.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {name} run {run}: {agent} answered {text}");
                }
            }
            if (scoring is not null)
            {
                var (found, asked, missed) = scoring.Score(result.Findings);
                caseFound += found;
                caseAsked += asked;
                if (missed.Count > 0)
                {
                    Console.WriteLine($"  {name} run {run}: suggestion missed {string.Join(", ", missed)}");
                }
            }
            totalCost += result.Usage is { } usage ? StepSummary.EstimateCost(usage) ?? 0 : 0;
            inputTokens += result.Usage?.InputTokens ?? 0;
            cachedTokens += result.Usage?.CachedInputTokens ?? 0;

            if (result.EvidenceDrops.Count > 0)
            {
                var drops = string.Join(", ", result.EvidenceDrops.Select(drop => $"{drop.Key}: {drop.Value}"));
                Console.WriteLine($"  {name} run {run}: the evidence check dropped {drops}");
            }

            if (problems.Count == 0)
            {
                passes++;
                // Replay runs without references, so answers given with them would be the wrong fixtures.
                if (recordDirectory is not null && !withReferences)
                {
                    foreach (var (agent, text) in recording.Answers)
                    {
                        var target = Path.Combine(recordDirectory, name, "answers");
                        Directory.CreateDirectory(target);
                        await File.WriteAllTextAsync(
                            Path.Combine(target, $"{agent}.json"),
                            JsonSerializer.Serialize(new RecordedAnswer(Prompts.Load(agent).Version, text), fileOptions) + "\n");
                    }
                }
            }
        }

        var heldOut = ExpectedCase.Load(directory).HeldOut;
        var label = heldOut ? " (held out)" : string.Empty;
        Console.WriteLine(scoring is null ? $"{name}{label}: {passes} of {runs}" : $"{name}{label}: {passes} of {runs}, suggestion concepts {caseFound} of {caseAsked}");
        conceptsFound += caseFound;
        conceptsAsked += caseAsked;
        tally.Add(heldOut, passes, runs, caseFound, caseAsked);
    }

    var versions = string.Join(", ", profiles.Select(profile => $"{profile} v{Prompts.Load(profile).Version}"));
    Console.WriteLine(!withReferences
        ? "Without reference material."
        : placement == ReferencePlacement.Finding
            ? $"With reference material per finding, added by {ReferenceSuggester.Name} v{Prompts.Load(ReferenceSuggester.Name).Version}."
            : $"With reference material, re-ranked by {ReferenceSelector.Name} v{Prompts.Load(ReferenceSelector.Name).Version}.");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Prompts {versions}, model {deployment}, estimated cost ~${totalCost:0.0000}."));
    Console.WriteLine($"Input tokens: {inputTokens}, of which cached: {cachedTokens}.");
    if (conceptsAsked > 0)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Suggestion concepts: {conceptsFound} of {conceptsAsked} ({100.0 * conceptsFound / conceptsAsked:0}%)."));
    }

    foreach (var line in tally.Lines())
    {
        Console.WriteLine(line);
    }

    Console.WriteLine(failures == 0 ? $"PASS: {selected.Length} case(s), {runs} run(s) each." : $"FAIL: {failures} failed run(s).");
    return failures == 0 ? 0 : 1;
}

async Task<ReviewResult> Review(
    string directory, IChatClient chat, string deployment = "replay", IReferenceRetriever? references = null, ReferencePlacement placement = ReferencePlacement.Finding)
{
    var scans = new RecordedScans(directory);
    var runner = new ReviewRunner(
        new LocalDirectorySource(standardsDirectory),
        new ConftestRunner(new ProcessRunner()),
        new ReviewModel(chat, deployment),
        infracost: new InfracostRunner(scans),
        checkov: new CheckovRunner(scans),
        references: references,
        placement: placement);

    // The demo's provider version, which is what the index holds docs for.
    const string LockFile = "provider \"registry.opentofu.org/hashicorp/azurerm\" {\n  version = \"5.12.0\"\n}\n";
    return await runner.RunAsync(
        await File.ReadAllTextAsync(Path.Combine(directory, "plan.json")),
        references is null ? Config : Config + "\nreferences: true\n",
        Commit,
        lockFile: references is null ? null : LockFile);
}

List<string> Problems(string directory, ReviewResult result) => Judge.Problems(ExpectedCase.Load(directory), result);

// Scores saved suggestions against the current patterns, so a pattern change is measured on every saved run alike.
// It takes what a live run saved with --suggestions (`<dir>/<case>/run-<n>.json`), or the docs/evals/runs/*.jsonl
// kept from those (one line per case and run), and reads files only.
int Rescore(string[] sources)
{
    foreach (var saved in sources)
    {
        if (SavedRuns(saved) is not { } runsByCase)
        {
            Console.Error.WriteLine($"No such directory or .jsonl file: {saved}");
            return 2;
        }

        var tally = new Tally();
        var found = 0;
        var asked = 0;
        foreach (var directory in cases)
        {
            var name = Path.GetFileName(directory);
            if (SuggestionScore.Load(directory) is not { } scoring || !runsByCase.TryGetValue(name, out var runs))
            {
                continue;
            }

            var caseFound = 0;
            var caseAsked = 0;
            foreach (var findings in runs)
            {
                var (f, a, _) = scoring.Score(findings);
                caseFound += f;
                caseAsked += a;
            }

            var heldOut = ExpectedCase.Load(directory).HeldOut;
            Console.WriteLine($"{saved} {name}{(heldOut ? " (held out)" : string.Empty)}: {caseFound} of {caseAsked}");
            found += caseFound;
            asked += caseAsked;
            tally.Add(heldOut, 0, 0, caseFound, caseAsked);
        }

        Console.WriteLine($"{saved}: suggestion concepts {found} of {asked}.");
        foreach (var line in tally.Lines(withRuns: false))
        {
            Console.WriteLine($"{saved}: {line}");
        }
    }

    return 0;
}

static Dictionary<string, List<List<ScoredFinding>>>? SavedRuns(string saved)
{
    var runs = new Dictionary<string, List<List<ScoredFinding>>>(StringComparer.Ordinal);
    if (saved.EndsWith(".jsonl", StringComparison.Ordinal) && File.Exists(saved))
    {
        foreach (var line in File.ReadLines(saved).Where(line => line.Length > 0))
        {
            var run = JsonDocument.Parse(line).RootElement;
            var name = run.GetProperty("case").GetString()!;
            runs.TryAdd(name, []);
            runs[name].Add(run.GetProperty("findings").Deserialize<List<ScoredFinding>>() ?? []);
        }

        return runs;
    }

    if (!Directory.Exists(saved))
    {
        return null;
    }

    foreach (var directory in Directory.GetDirectories(saved))
    {
        runs[Path.GetFileName(directory)] =
        [
            .. Directory.GetFiles(directory, "run-*.json").Order(StringComparer.Ordinal).Select(file =>
                JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("findings").Deserialize<List<ScoredFinding>>() ?? []),
        ];
    }

    return runs;
}

static int Report(string name, int run, IReadOnlyList<string> problems)
{
    foreach (var problem in problems)
    {
        Console.WriteLine($"FAILED {name} run {run}: {problem}");
    }

    return problems.Count == 0 ? 0 : 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} isn't set.");

internal sealed record RecordedAnswer(
    [property: JsonPropertyName("prompt_version")] int PromptVersion,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Which scan a scanner run is, and so which file holds its output.</summary>
internal static class ScanFile
{
    public static string Name(string program, IReadOnlyList<string> arguments)
    {
        if (program == "checkov")
        {
            return "checkov.scan";
        }

        return arguments[1].EndsWith("before.json", StringComparison.Ordinal) ? "infracost-before.scan" : "infracost-after.scan";
    }
}

/// <summary>Plays back saved scanner output. A missing file fails the scan, and so the case.</summary>
internal sealed class RecordedScans(string directory) : IProcessRunner
{
    public async Task<ProcessResult?> RunAsync(
        string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, ScanFile.Name(program, arguments));
        return File.Exists(path) ? new ProcessResult(0, await File.ReadAllTextAsync(path, cancellationToken)) : null;
    }
}

/// <summary>Runs the real scanner and saves its output, with GUIDs zeroed, because these files are public.</summary>
internal sealed partial class RecordingScans(IProcessRunner inner, string directory) : IProcessRunner
{
    public async Task<ProcessResult?> RunAsync(
        string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await inner.RunAsync(program, arguments, timeout, cancellationToken);
        if (result is not null)
        {
            var clean = Guid().Replace(result.Output, "00000000-0000-0000-0000-000000000000");
            await File.WriteAllTextAsync(Path.Combine(directory, ScanFile.Name(program, arguments)), clean, cancellationToken);
        }

        return result;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();
}

/// <summary>Works out which agent a request is from, because each has its own prompt.</summary>
internal static class AgentOf
{
    public static string? Request(ChatOptions? options, IEnumerable<string> profiles) =>
        profiles.FirstOrDefault(profile => options?.Instructions?.StartsWith(Prompts.Load(profile).Body, StringComparison.Ordinal) == true);
}

/// <summary>Gives each agent its recorded answer, for runs that mustn't reach the model. No answer is a failure.</summary>
internal sealed class ReplayChat(IReadOnlyDictionary<string, string> answers, IEnumerable<string> profiles) : IChatClient
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> missing = new();

    /// <summary>
    /// Requests that had no recorded answer. The orchestrator and the arbiter fall back to rules, and the suggester to
    /// adding nothing, when a call throws, so the case would still pass: the replay reports these as failures instead.
    /// </summary>
    public IReadOnlyList<string> Missing => [.. missing.Distinct(StringComparer.Ordinal)];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var agent = AgentOf.Request(options, profiles);
        if (agent is null || !answers.TryGetValue(agent, out var text))
        {
            missing.Enqueue($"no recorded answer for {agent ?? "a request from an unknown role"}");
            throw new InvalidOperationException($"There's no recorded answer for {agent ?? "this request"}.");
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Keeps each agent's last answer, so a passing run can become a replay.</summary>
internal sealed class RecordingChat(IChatClient inner, IEnumerable<string> profiles) : DelegatingChatClient(inner)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> answers = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Answers => answers;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        if (AgentOf.Request(options, profiles) is { } agent)
        {
            answers[agent] = response.Text;
        }

        return response;
    }

    // The client is shared across runs, so disposing one run's wrapper mustn't dispose it.
    protected override void Dispose(bool disposing)
    {
    }
}
