using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TfReview.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TfReview.Corpus;

public sealed class SourcesException(string message) : Exception(message);

/// <summary>One repo at one commit to import from. A provider has one of these per version.</summary>
public sealed record SourcePin(
    string Id,
    string Repository,
    string Commit,
    string License,
    string Attribution,
    IReadOnlyList<string> Include,
    string? ProviderVersion)
{
    /// <summary>Paths under an include to leave out.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>corpus/sources.yml, checked. It decides what reaches an index, so anything unexpected stops the run.</summary>
public sealed partial class CorpusSources
{
    /// <summary>Changing how text is split changes every chunk's hash, so the next run embeds everything again (D44).</summary>
    public const string ChunkerVersion = "1";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    // D41. CIS isn't here because its benchmark forbids hosting, and a licence not on this list can't get in.
    private static readonly string[] Licenses = ["MIT", "CC-BY-4.0", "MPL-2.0"];

    private static readonly string[] SourceIds = ["azure-policy", "cloud-adoption-framework", "azurerm-docs"];

    private CorpusSources(string embeddingModel, IReadOnlyList<SourcePin> pins)
    {
        this.EmbeddingModel = embeddingModel;
        this.Pins = pins;
    }

    public string EmbeddingModel { get; }

    public IReadOnlyList<SourcePin> Pins { get; }

    /// <summary>
    /// Everything that decides the index's content, so the same pins always name the same version, and reverting a
    /// pin change returns to it.
    /// </summary>
    public string CorpusVersion
    {
        get
        {
            var inputs = new StringBuilder().AppendLine(this.EmbeddingModel).AppendLine(ChunkerVersion);
            foreach (var pin in this.Pins)
            {
                inputs.AppendLine($"{pin.Id} {pin.Repository} {pin.Commit} {pin.ProviderVersion} {string.Join(',', pin.Include)} {string.Join(',', pin.Exclude)}");
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())))[..16];
        }
    }

    public static CorpusSources Parse(string yaml)
    {
        SourcesFile file;
        try
        {
            file = Yaml.Deserialize<SourcesFile>(yaml) ?? throw new SourcesException("The sources file is empty.");
        }
        catch (YamlException e)
        {
            throw new SourcesException($"The sources file can't be read: {e.Message}");
        }

        if (file.Version != 1)
        {
            throw new SourcesException($"Unsupported version {file.Version}.");
        }

        if (string.IsNullOrWhiteSpace(file.EmbeddingModel))
        {
            throw new SourcesException("embedding_model is required.");
        }

        var pins = (file.Sources ?? []).SelectMany(ToPins).ToList();
        if (pins.Count == 0)
        {
            throw new SourcesException("There are no sources.");
        }

        var repeated = pins.GroupBy(pin => (pin.Id, pin.ProviderVersion)).FirstOrDefault(group => group.Count() > 1);
        return repeated is null
            ? new CorpusSources(file.EmbeddingModel, pins)
            : throw new SourcesException($"{repeated.Key.Id} is listed twice.");
    }

    private static IEnumerable<SourcePin> ToPins(SourceEntry entry)
    {
        var id = entry.Id ?? throw new SourcesException("A source has no id.");
        if (!SourceIds.Contains(id, StringComparer.Ordinal))
        {
            throw new SourcesException($"{id} isn't a source with an importer.");
        }

        if (!Licenses.Contains(entry.License, StringComparer.Ordinal))
        {
            throw new SourcesException($"{id} has the licence {entry.License ?? "(none)"}, which isn't allowed (D41).");
        }

        if (string.IsNullOrWhiteSpace(entry.Attribution))
        {
            throw new SourcesException($"{id} has no attribution.");
        }

        if (entry.Repository is null || !RepositoryFormat().IsMatch(entry.Repository))
        {
            throw new SourcesException($"{id} has no valid repository.");
        }

        if (entry.Include is not { Count: > 0 } include || include.Any(path => !PathFormat().IsMatch(path) || path.Split('/').Contains("..")))
        {
            throw new SourcesException($"{id} needs include paths inside its repo.");
        }

        var exclude = entry.Exclude ?? [];
        if (exclude.Any(path => !ExcludeFormat().IsMatch(path) || path.Split('/').Contains("..")))
        {
            throw new SourcesException($"{id} needs exclude paths inside its repo.");
        }

        var isProvider = id == "azurerm-docs";
        if (isProvider == entry.Versions is null || isProvider == entry.Commit is not null)
        {
            throw new SourcesException($"{id} needs {(isProvider ? "versions and no commit" : "a commit and no versions")}.");
        }

        foreach (var (commit, version) in isProvider
            ? entry.Versions!.Select(item => (item.Commit, (string?)item.Version))
            : [(entry.Commit, null)])
        {
            if (commit is null || !CommitFormat().IsMatch(commit))
            {
                throw new SourcesException($"{id} has to pin a full commit.");
            }

            if (isProvider && (version is null || !VersionFormat().IsMatch(version)))
            {
                throw new SourcesException($"{id} has an invalid version.");
            }

            yield return new SourcePin(id, entry.Repository, commit, entry.License!, entry.Attribution, include, version) { Exclude = exclude };
        }
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitFormat();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryFormat();

    [GeneratedRegex("^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$")]
    private static partial Regex PathFormat();

    // Upstream's folder names can have spaces.
    [GeneratedRegex("^[A-Za-z0-9_. -]+(/[A-Za-z0-9_. -]+)*$")]
    private static partial Regex ExcludeFormat();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex VersionFormat();

    private sealed class SourcesFile
    {
        public int Version { get; set; }

        public string? EmbeddingModel { get; set; }

        public List<SourceEntry>? Sources { get; set; }
    }

    private sealed class SourceEntry
    {
        public string? Id { get; set; }

        public string? Repository { get; set; }

        public string? Commit { get; set; }

        public string? License { get; set; }

        public string? Attribution { get; set; }

        public List<string>? Include { get; set; }

        public List<string>? Exclude { get; set; }

        public List<VersionEntry>? Versions { get; set; }
    }

    private sealed class VersionEntry
    {
        public string? Version { get; set; }

        public string? Commit { get; set; }
    }
}
