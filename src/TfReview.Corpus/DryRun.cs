using System.Globalization;
using System.Text;
using TfReview.Core;

namespace TfReview.Corpus;

/// <summary>
/// What a change to corpus/sources.yml would do to the index, which is how a pin change gets reviewed, because the
/// upstream text itself isn't in the diff (D44). It holds counts, paths and links, not upstream text.
/// </summary>
public static class DryRun
{
    // About $0.02 per million tokens, from third-party listings of the Data Zone rate, not an invoice.
    public const decimal EmbeddingUsdPerToken = 0.02m / 1_000_000m;

    public static string Report(
        CorpusSources? before, IReadOnlyList<ImportReport> beforeReports, CorpusSources after, IReadOnlyList<ImportReport> afterReports)
    {
        var report = new StringBuilder()
            .AppendLine("### Reference corpus dry run")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Corpus version `{before?.CorpusVersion ?? "none"}` becomes `{after.CorpusVersion}`.")
            .AppendLine()
            .AppendLine("| Source | Docs | Added | Changed | Removed | Chunks | To embed | Upstream changes |")
            .AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        var knownHashes = beforeReports.SelectMany(item => item.Chunks).Select(chunk => chunk.ContentHash).ToHashSet(StringComparer.Ordinal);
        long tokensToEmbed = 0;

        var keys = beforeReports.Select(Key).Concat(afterReports.Select(Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var old = beforeReports.FirstOrDefault(item => Key(item) == key);
            var updated = afterReports.FirstOrDefault(item => Key(item) == key);

            var oldDocs = Docs(old);
            var newDocs = Docs(updated);
            var added = newDocs.Keys.Count(path => !oldDocs.ContainsKey(path));
            var removed = oldDocs.Keys.Count(path => !newDocs.ContainsKey(path));
            var changed = newDocs.Count(doc => oldDocs.TryGetValue(doc.Key, out var hashes) && hashes != doc.Value);

            var embed = updated?.Chunks.Where(chunk => !knownHashes.Contains(chunk.ContentHash)).DistinctBy(chunk => chunk.ContentHash).ToList() ?? [];
            tokensToEmbed += embed.Sum(chunk => (long)TokenEstimate.Of(chunk.ToEmbed));

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {key} | {newDocs.Count} | {added} | {changed} | {removed} | {updated?.Chunks.Count ?? 0} | {embed.Count} | {Comparison(old?.Pin, updated?.Pin)} |");
        }

        return report
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"Embedding the new chunks: at most {tokensToEmbed} tokens, at most ${tokensToEmbed * EmbeddingUsdPerToken:0.00}."))
            .AppendLine()
            .AppendLine("Upstream text is untrusted. It never creates a finding, and reviews show it in its own fence (D42).")
            .ToString();
    }

    private static string Key(ImportReport report) =>
        report.Pin.ProviderVersion is { } version ? $"{report.Pin.Id}@{version}" : report.Pin.Id;

    /// <summary>Each document's chunk hashes, so a change to any part of it shows as a change to the document.</summary>
    private static Dictionary<string, string> Docs(ImportReport? report) =>
        report?.Chunks.GroupBy(chunk => chunk.SourcePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join(',', group.Select(chunk => chunk.ContentHash)), StringComparer.Ordinal)
        ?? [];

    private static string Comparison(SourcePin? before, SourcePin? after) => (before, after) switch
    {
        (null, { } added) => $"[added at `{added.Commit[..7]}`](https://github.com/{added.Repository}/tree/{added.Commit})",
        ({ }, null) => "removed",
        ({ } old, { } updated) when old.Commit == updated.Commit => "none",
        ({ } old, { } updated) => $"[`{old.Commit[..7]}...{updated.Commit[..7]}`](https://github.com/{updated.Repository}/compare/{old.Commit}...{updated.Commit})",
        _ => string.Empty,
    };
}
