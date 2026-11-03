using System.Globalization;
using System.Text.Json;
using TfReview.Core;
using TfReview.Corpus;

const string Usage = """
    usage: tfreview-corpus import <sources.yml> <chunks.jsonl> [archive-directory]
           tfreview-corpus dry-run <base-sources.yml> <sources.yml> <report.md>
           tfreview-corpus index <sources.yml> [--rebuild]

    import and dry-run need no credentials and write nothing to Azure. A base sources file that doesn't exist counts as
    no sources. An archive directory holds archives already downloaded, as <owner>_<repo>-<commit>.tar.gz.
    index writes the reference container, as the identity in its environment: TF_REVIEW_COSMOS_ENDPOINT and
    TF_REVIEW_OPENAI_ENDPOINT name where, and the embedding model in the sources file is the deployment's name.
    """;

try
{
    return args switch
    {
        ["import", var sources, var output] => await Import(sources, output, null),
        ["import", var sources, var output, var archives] => await Import(sources, output, archives),
        ["dry-run", var baseSources, var sources, var report] => await DryRunAsync(baseSources, sources, report),
        ["index", var sources] => await Index(sources, rebuild: false),
        ["index", var sources, "--rebuild"] => await Index(sources, rebuild: true),
        _ => Fail(),
    };
}
catch (Exception e) when (e is SourcesException or ArchiveException or LicenseMismatchException or HttpRequestException or IndexIncompleteException)
{
    Console.Error.WriteLine($"Failed: {e.Message}");
    return 1;
}

static int Fail()
{
    Console.Error.WriteLine(Usage);
    return 2;
}

static async Task<int> Import(string sourcesPath, string outputPath, string? archives)
{
    var sources = CorpusSources.Parse(await File.ReadAllTextAsync(sourcesPath));
    Console.WriteLine($"Corpus version {sources.CorpusVersion}, embedding with {sources.EmbeddingModel}.");

    await using var output = new StreamWriter(outputPath);
    long totalTokens = 0;
    foreach (var report in await ImportAll(sources, archives))
    {
        foreach (var chunk in report.Chunks)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(chunk));
        }

        totalTokens += report.Chunks.Sum(chunk => (long)TokenEstimate.Of(chunk.ToEmbed));
        var characters = report.Chunks.Sum(chunk => (long)chunk.Text.Length);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name(report.Pin)}: {report.Documents} docs, {report.Chunks.Count} chunks, {characters / 1_000_000m:0.0} MB of text, {report.Skipped} skipped, {report.TooLarge} too large."));
    }

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Total: at most {totalTokens} tokens, at most ${totalTokens * DryRun.EmbeddingUsdPerToken:0.00} to embed from nothing."));
    return 0;
}

static async Task<int> DryRunAsync(string baseSourcesPath, string sourcesPath, string reportPath)
{
    var after = CorpusSources.Parse(await File.ReadAllTextAsync(sourcesPath));
    var before = File.Exists(baseSourcesPath) ? CorpusSources.Parse(await File.ReadAllTextAsync(baseSourcesPath)) : null;

    var afterReports = await ImportAll(after, null);

    // An unchanged pin imports the same chunks, so it's only read once.
    var beforeReports = new List<ImportReport>();
    foreach (var pin in before?.Pins ?? [])
    {
        beforeReports.Add(afterReports.FirstOrDefault(report => report.Pin == pin && before!.EmbeddingModel == after.EmbeddingModel)
            ?? await ImportOne(pin, before!, null));
    }

    var report = DryRun.Report(before, beforeReports, after, afterReports);
    await File.WriteAllTextAsync(reportPath, report);
    Console.WriteLine(report);
    return 0;
}

static async Task<int> Index(string sourcesPath, bool rebuild)
{
    var sources = CorpusSources.Parse(await File.ReadAllTextAsync(sourcesPath));
    var reports = await ImportAll(sources, null);
    var chunks = reports.SelectMany(report => report.Chunks).ToList();

    var credential = IndexCredential.Create();
    var store = CosmosReferenceStore.Create(Required("TF_REVIEW_COSMOS_ENDPOINT"), credential);
    var embedder = AzureEmbedder.Create(new Uri(Required("TF_REVIEW_OPENAI_ENDPOINT")), sources.EmbeddingModel, credential);

    var outcome = await new Indexer(store, embedder, Console.WriteLine).RunAsync(chunks, sources.CorpusVersion, rebuild);
    Console.WriteLine(
        $"Version {outcome.Version}: {outcome.Chunks} chunks, {outcome.Reused} reused, {outcome.Embedded} embedded, "
        + $"{(outcome.Switched ? "now in use" : "already in use")}, {outcome.Deleted.Count} old version(s) deleted.");
    return 0;
}

static async Task<List<ImportReport>> ImportAll(CorpusSources sources, string? archives)
{
    var reports = new List<ImportReport>();
    foreach (var pin in sources.Pins)
    {
        reports.Add(await ImportOne(pin, sources, archives));
    }

    return reports;
}

static async Task<ImportReport> ImportOne(SourcePin pin, CorpusSources sources, string? archives)
{
    await using var archive = await Open(pin, archives);
    return await CorpusImport.ImportAsync(pin, archive, sources.CorpusVersion, sources.EmbeddingModel);
}

static async Task<Stream> Open(SourcePin pin, string? directory)
{
    if (directory is not null)
    {
        return File.OpenRead(Path.Combine(directory, $"{pin.Repository.Replace('/', '_')}-{pin.Commit}.tar.gz"));
    }

    // Public repos, so no token: the dry run runs PR code and holds no credentials at all.
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("tf-review-corpus");
    var response = await http.GetAsync(new Uri($"https://codeload.github.com/{pin.Repository}/tar.gz/{pin.Commit}"), HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStreamAsync();
}

static string Name(SourcePin pin) => pin.ProviderVersion is { } version ? $"{pin.Id}@{version}" : pin.Id;

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value
    : throw new SourcesException($"{name} isn't set.");
