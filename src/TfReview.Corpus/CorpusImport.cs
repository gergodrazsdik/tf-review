namespace TfReview.Corpus;

public sealed class LicenseMismatchException(string message) : Exception(message);

/// <summary>What importing one pin came to, for the dry run's report.</summary>
public sealed record ImportReport(SourcePin Pin, int Documents, int Skipped, int TooLarge, IReadOnlyList<ReferenceChunk> Chunks);

/// <summary>Imports one pin from its archive, after checking it's really under the licence the sources file says.</summary>
public static class CorpusImport
{
    // Words only that licence's text has. sources.yml says what a source's licence should be, and this checks upstream
    // still agrees at the pinned commit, because a repo can change its licence and a pin change wouldn't show it.
    private static readonly Dictionary<string, string> LicenseMarkers = new(StringComparer.Ordinal)
    {
        ["MIT"] = "Permission is hereby granted, free of charge",
        ["CC-BY-4.0"] = "Attribution 4.0 International",
        ["MPL-2.0"] = "Mozilla Public License",
    };

    public static async Task<ImportReport> ImportAsync(
        SourcePin pin, Stream archive, string corpusVersion, string embeddingModel, CancellationToken cancellationToken = default)
    {
        var contents = await UpstreamArchive.ReadAsync(archive, path => Importers.Wants(pin, path), cancellationToken);
        CheckLicense(pin, contents.License);

        var (chunks, documents, skipped) = Importers.Import(pin, contents.Files, corpusVersion, embeddingModel);
        return new ImportReport(pin, documents, skipped, contents.TooLarge, chunks);
    }

    /// <summary>Nothing from this source gets in unless its own licence file says what the sources file says.</summary>
    internal static void CheckLicense(SourcePin pin, string? license)
    {
        if (license is null)
        {
            throw new LicenseMismatchException($"{pin.Id} has no licence file at {pin.Commit}.");
        }

        if (!LicenseMarkers.TryGetValue(pin.License, out var marker)
            || !license.ReplaceLineEndings(" ").Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            throw new LicenseMismatchException($"{pin.Id}'s licence file at {pin.Commit} isn't {pin.License}.");
        }
    }
}
