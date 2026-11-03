using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TfReview.Core;

namespace TfReview.Corpus;

/// <summary>One piece of an imported document, as docs/contracts/reference-chunk.schema.json defines it.</summary>
public sealed record ReferenceChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("corpus_version")] string CorpusVersion,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("source_repository")] string SourceRepository,
    [property: JsonPropertyName("source_commit")] string SourceCommit,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_url")] string SourceUrl,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("attribution")] string Attribution,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("heading")] string Heading,
    [property: JsonPropertyName("applies_to")] IReadOnlyList<string> AppliesTo,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("content_hash")] string ContentHash)
{
    [JsonPropertyName("provider_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderVersion { get; init; }
}

/// <summary>What one document became: its title, what it's about, and its sections. Null if it isn't worth keeping.</summary>
internal sealed record ImportedDoc(string Title, IReadOnlyList<string> AppliesTo, IReadOnlyList<DocSection> Sections);

internal sealed record DocSection(string Heading, string Text);

/// <summary>Turns a source's files into chunks. Upstream's text is untrusted, and only ever stored, never acted on.</summary>
public static partial class Importers
{
    /// <summary>Upstream's own text, cut to what embeds well and a prompt can hold. The contract allows 8,000.</summary>
    internal const int MaxChunkCharacters = 4_000;

    private const int MaxRuleCharacters = 1_500;

    // Sections that say the same on nearly every page, which would fill search results with near-copies.
    private static readonly string[] BoilerplateHeadings = ["Import", "Timeouts", "API Providers"];

    // Only links to other pages, which say nothing by themselves.
    private static readonly string[] LinkHeadings = ["Next step", "Next steps", "Related content", "Related resources"];

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Whether a file is one this source's importer reads.</summary>
    public static bool Wants(SourcePin pin, string path) =>
        pin.Include.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal))
        && !pin.Exclude.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal))
        && pin.Id switch
        {
            "azure-policy" => path.EndsWith(".json", StringComparison.Ordinal),
            "azurerm-docs" => path.EndsWith(".html.markdown", StringComparison.Ordinal),
            _ => path.EndsWith(".md", StringComparison.Ordinal) && !path.Contains("/includes/", StringComparison.Ordinal),
        };

    /// <summary>A source's chunks, in a stable order, and how many of its files had nothing to keep.</summary>
    public static (IReadOnlyList<ReferenceChunk> Chunks, int Documents, int Skipped) Import(
        SourcePin pin, IEnumerable<UpstreamFile> files, string corpusVersion, string embeddingModel)
    {
        var chunks = new List<ReferenceChunk>();
        var documents = 0;
        var skipped = 0;

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var text = Encoding.UTF8.GetString(file.Content).TrimStart('﻿');
            var doc = pin.Id switch
            {
                "azure-policy" => ReadPolicy(text),
                "azurerm-docs" => ReadProviderDoc(file.Path, text),
                _ => ReadFrameworkPage(text),
            };

            if (doc is null)
            {
                skipped++;
                continue;
            }

            documents++;
            chunks.AddRange(ToChunks(pin, file.Path, doc, corpusVersion, embeddingModel));
        }

        return (chunks, documents, skipped);
    }

    /// <summary>
    /// Sections by second-level heading, each split to fit. A heading inside a code fence isn't a heading.
    /// </summary>
    internal static IReadOnlyList<DocSection> SplitByHeading(string markdown)
    {
        var sections = new List<DocSection>();
        var heading = string.Empty;
        var current = new StringBuilder();
        var fenced = false;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            if (!fenced && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Add();
                heading = line[3..].Trim();
                current.Clear();
                continue;
            }

            current.AppendLine(line);
        }

        Add();
        return sections;

        void Add()
        {
            if (current.ToString().Trim() is { Length: > 0 } text)
            {
                sections.AddRange(Fit(text).Select(part => new DocSection(heading, part)));
            }
        }
    }

    /// <summary>Splits at blank lines where it can, and anywhere when one paragraph is too long by itself.</summary>
    internal static IEnumerable<string> Fit(string text)
    {
        if (text.Length <= MaxChunkCharacters)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > MaxChunkCharacters)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            var rest = paragraph;
            while (rest.Length > MaxChunkCharacters)
            {
                yield return rest[..MaxChunkCharacters];
                rest = rest[MaxChunkCharacters..];
            }

            current.Append(rest).Append("\n\n");
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    private static List<ReferenceChunk> ToChunks(SourcePin pin, string path, ImportedDoc doc, string corpusVersion, string embeddingModel)
    {
        var chunks = new List<ReferenceChunk>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in doc.Sections)
        {
            // A section's position among those sharing its heading, so adding one elsewhere doesn't move the others' IDs.
            var ordinal = seen.GetValueOrDefault(section.Heading);
            seen[section.Heading] = ordinal + 1;

            chunks.Add(new ReferenceChunk(
                ReferenceDocs.IdPrefix + Hash($"{pin.Id}\n{pin.ProviderVersion}\n{path}\n{section.Heading}\n{ordinal}")[..32],
                corpusVersion,
                pin.Id,
                pin.Repository,
                pin.Commit,
                path,
                $"https://github.com/{pin.Repository}/blob/{pin.Commit}/{path}",
                pin.License,
                pin.Attribution,
                doc.Title,
                section.Heading,
                doc.AppliesTo,
                section.Text,
                Hash($"{section.Text}\n{embeddingModel}\n{CorpusSources.ChunkerVersion}"))
            {
                ProviderVersion = pin.ProviderVersion,
            });
        }

        return chunks;
    }

    /// <summary>One chunk per policy: what it says and which Azure types it looks at, which a plan's change can match.</summary>
    private static ImportedDoc? ReadPolicy(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !TryString(properties, "displayName", out var title))
            {
                return null;
            }

            var hasMetadata = properties.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object;
            var deprecated = title.StartsWith("[Deprecated]", StringComparison.OrdinalIgnoreCase)
                || (hasMetadata && metadata.TryGetProperty("deprecated", out var flag) && flag.ValueKind == JsonValueKind.True);
            if (deprecated)
            {
                return null;
            }

            var text = new StringBuilder(title).AppendLine();
            if (TryString(properties, "description", out var description))
            {
                text.AppendLine(description);
            }

            if (hasMetadata && TryString(metadata, "category", out var category))
            {
                text.AppendLine($"Category: {category}");
            }

            if (properties.TryGetProperty("policyRule", out var rule))
            {
                var raw = rule.GetRawText();
                var types = AzureType().Matches(raw).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (types.Count > 0)
                {
                    text.AppendLine($"Azure types: {string.Join(", ", types)}");
                }

                text.AppendLine($"Rule: {(raw.Length > MaxRuleCharacters ? raw[..MaxRuleCharacters] : raw)}");
            }

            // Azure's types aren't azurerm's, and no list maps them, so these are found by search, not by filter.
            return new ImportedDoc(title, [], [.. Fit(text.ToString().Trim()).Select(part => new DocSection(string.Empty, part))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A resource's page, whose file name gives its type, which is what makes it filterable by a plan.</summary>
    private static ImportedDoc? ReadProviderDoc(string path, string markdown)
    {
        var name = System.IO.Path.GetFileName(path).Replace(".html.markdown", string.Empty, StringComparison.Ordinal);
        if (!ResourceName().IsMatch(name))
        {
            return null;
        }

        var type = "azurerm_" + name;
        var sections = SplitByHeading(WithoutFrontmatter(markdown).Body)
            .Where(section => !BoilerplateHeadings.Contains(section.Heading, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return sections.Count == 0 ? null : new ImportedDoc(type, [type], sections);
    }

    /// <summary>A page about running Azure, which isn't about a particular resource type.</summary>
    private static ImportedDoc? ReadFrameworkPage(string markdown)
    {
        var (frontmatter, body) = WithoutFrontmatter(markdown);
        if (frontmatter.Contains("redirect_url:", StringComparison.Ordinal))
        {
            return null;
        }

        var title = FrontmatterTitle().Match(frontmatter) is { Success: true } match
            ? match.Groups[1].Value.Trim().Trim('"', '\'')
            : FirstHeading().Match(body).Groups[1].Value.Trim();
        var sections = SplitByHeading(body).Where(section => !LinkHeadings.Contains(section.Heading, StringComparer.OrdinalIgnoreCase)).ToList();
        return sections.Count == 0 || title.Length == 0 ? null : new ImportedDoc(title, [], sections);
    }

    private static (string Frontmatter, string Body) WithoutFrontmatter(string markdown)
    {
        var text = markdown.ReplaceLineEndings("\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal)
            && text.IndexOf("\n---\n", 3, StringComparison.Ordinal) is var end and > 0)
        {
            return (text[4..end], text[(end + 5)..]);
        }

        return (string.Empty, text);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex("Microsoft\\.[A-Za-z0-9]+(/[A-Za-z0-9]+)+")]
    private static partial Regex AzureType();

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex ResourceName();

    [GeneratedRegex("^title:(.+)$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterTitle();

    [GeneratedRegex("^# (.+)$", RegexOptions.Multiline)]
    private static partial Regex FirstHeading();
}
