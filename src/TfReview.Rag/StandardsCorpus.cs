using System.Text.RegularExpressions;
using TfReview.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TfReview.Rag;

public sealed class CorpusException(string message) : Exception(message);

/// <summary>One standards document, as docs/contracts/corpus-frontmatter.schema.json defines its frontmatter.</summary>
public sealed record StandardsDoc(
    string Id,
    string Status,
    Severity Severity,
    IReadOnlyList<string> AppliesTo,
    IReadOnlyList<string> Topics,
    string Path,
    string Body)
{
    public const string Accepted = "accepted";

    public bool AppliesToType(string resourceType) =>
        AppliesTo.Contains("*", StringComparer.Ordinal) || AppliesTo.Contains(resourceType, StringComparer.Ordinal);
}

/// <summary>
/// The documents in an unpacked corpus. A document with broken frontmatter throws rather than being
/// skipped, because skipping it would silently review against fewer standards than the repo asked for.
/// </summary>
public sealed partial class StandardsCorpus
{
    // The directories tf-standards' own frontmatter check covers. Other Markdown, such as agent rules
    // under .claude, can have frontmatter that means something else.
    private static readonly string[] CorpusDirectories = ["adr", "policies", "design"];

    private static readonly string[] Statuses = ["proposed", StandardsDoc.Accepted, "superseded", "deprecated"];

    private static readonly string[] KnownTopics = ["naming", "tagging", "modules", "security", "network", "cost"];

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private StandardsCorpus(IReadOnlyList<StandardsDoc> docs) => Docs = docs;

    /// <summary>Every document with frontmatter, sorted by ID so prompts built from them are stable.</summary>
    public IReadOnlyList<StandardsDoc> Docs { get; }

    public static async Task<StandardsCorpus> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var docs = new List<StandardsDoc>();
        foreach (var corpusDirectory in CorpusDirectories)
        {
            var path = Path.Combine(directory, corpusDirectory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken);
                if (Read(text, Path.GetRelativePath(directory, file)) is { } doc)
                {
                    docs.Add(doc);
                }
            }
        }

        if (docs.DistinctBy(doc => doc.Id).Count() != docs.Count)
        {
            throw new CorpusException("Two documents have the same ID.");
        }

        return new StandardsCorpus([.. docs.OrderBy(doc => doc.Id, StringComparer.Ordinal)]);
    }

    /// <summary>Reads one document, or returns null for one with no frontmatter, such as a README.</summary>
    private static StandardsDoc? Read(string text, string path)
    {
        const string Fence = "---\n";

        var normalized = text.ReplaceLineEndings("\n");
        if (!normalized.StartsWith(Fence, StringComparison.Ordinal))
        {
            return null;
        }

        var end = normalized.IndexOf("\n" + Fence, Fence.Length - 1, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new CorpusException($"{path}: the frontmatter isn't closed.");
        }

        Frontmatter? frontmatter;
        try
        {
            frontmatter = Yaml.Deserialize<Frontmatter>(normalized[Fence.Length..(end + 1)]);
        }
        catch (YamlException)
        {
            throw new CorpusException($"{path}: the frontmatter isn't valid YAML, or has an unknown field.");
        }

        return Validate(frontmatter, path, normalized[(end + 1 + Fence.Length)..].Trim());
    }

    private static StandardsDoc Validate(Frontmatter? frontmatter, string path, string body)
    {
        if (frontmatter?.Id is not { } id || !DocId().IsMatch(id))
        {
            throw new CorpusException($"{path}: id is missing or malformed.");
        }

        if (frontmatter.Status is not { } status || !Statuses.Contains(status, StringComparer.Ordinal))
        {
            throw new CorpusException($"{path}: status is missing or unknown.");
        }

        if (frontmatter.AppliesTo is not { Count: > 0 } appliesTo || !appliesTo.All(type => ResourceType().IsMatch(type)))
        {
            throw new CorpusException($"{path}: applies_to is missing or malformed.");
        }

        if (frontmatter.Topics is not { Count: > 0 } topics || !topics.All(topic => KnownTopics.Contains(topic, StringComparer.Ordinal)))
        {
            throw new CorpusException($"{path}: topics is missing or has an unknown topic.");
        }

        var severity = frontmatter.Severity switch
        {
            "low" => Severity.Low,
            "medium" => Severity.Medium,
            "high" => Severity.High,
            _ => throw new CorpusException($"{path}: severity is missing or unknown."),
        };

        return new StandardsDoc(id, status, severity, appliesTo, topics, path.Replace('\\', '/'), body);
    }

    [GeneratedRegex("^(ADR|POL|DES)-[0-9]{3}$")]
    private static partial Regex DocId();

    [GeneratedRegex(@"^(\*|[a-z0-9_]+)$")]
    private static partial Regex ResourceType();

    private sealed class Frontmatter
    {
        public string? Id { get; set; }

        public string? Type { get; set; }

        public string? Status { get; set; }

        public string? SupersededBy { get; set; }

        public List<string>? AppliesTo { get; set; }

        public List<string>? Topics { get; set; }

        public string? Severity { get; set; }

        public string? Rego { get; set; }
    }
}
