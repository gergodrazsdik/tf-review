using System.Globalization;
using System.Text.Json;
using TfReview.Core;
using TfReview.Corpus;

const string Usage = """
    usage: tfreview-corpus import <sources.yml> <chunks.jsonl> [archive-directory]

    Pulls each source at its pinned commit, and writes its chunks with a report of what that came to. A directory holds
    archives already downloaded, as <owner>_<repo>-<commit>.tar.gz. Needs no credentials, and writes nothing to Azure.
    """;

// About $0.02 per million tokens, from third-party listings of the Data Zone rate, not an invoice.
const decimal EmbeddingUsdPerToken = 0.02m / 1_000_000m;

if (args is not ["import", var sourcesPath, var outputPath, ..] || args.Length > 4)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var archives = args.Length == 4 ? args[3] : null;

try
{
    var sources = CorpusSources.Parse(await File.ReadAllTextAsync(sourcesPath));
    var version = sources.CorpusVersion;
    Console.WriteLine($"Corpus version {version}, embedding with {sources.EmbeddingModel}.");

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("tf-review-corpus");

    await using var output = new StreamWriter(outputPath);
    long totalChunks = 0;
    long totalTokens = 0;

    foreach (var pin in sources.Pins)
    {
        await using var archive = await OpenAsync(http, pin, archives);
        var report = await CorpusImport.ImportAsync(pin, archive, version, sources.EmbeddingModel);

        foreach (var chunk in report.Chunks)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(chunk));
        }

        var tokens = report.Chunks.Sum(chunk => (long)TokenEstimate.Of(chunk.Text));
        var characters = report.Chunks.Sum(chunk => (long)chunk.Text.Length);
        totalChunks += report.Chunks.Count;
        totalTokens += tokens;

        var name = pin.ProviderVersion is { } provider ? $"{pin.Id}@{provider}" : pin.Id;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {report.Documents} docs, {report.Chunks.Count} chunks, {characters / 1_000_000m:0.0} MB of text, {report.Skipped} skipped, {report.TooLarge} too large."));
    }

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Total: {totalChunks} chunks, at most {totalTokens} tokens, at most ${totalTokens * EmbeddingUsdPerToken:0.00} to embed from nothing."));
    return 0;
}
catch (Exception e) when (e is SourcesException or ArchiveException or LicenseMismatchException or HttpRequestException)
{
    Console.Error.WriteLine($"Import failed: {e.Message}");
    return 1;
}

static async Task<Stream> OpenAsync(HttpClient http, SourcePin pin, string? directory)
{
    if (directory is not null)
    {
        return File.OpenRead(Path.Combine(directory, $"{pin.Repository.Replace('/', '_')}-{pin.Commit}.tar.gz"));
    }

    var response = await http.GetAsync(new Uri($"https://codeload.github.com/{pin.Repository}/tar.gz/{pin.Commit}"), HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStreamAsync();
}
