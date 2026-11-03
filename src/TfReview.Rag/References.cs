using System.Text.RegularExpressions;

namespace TfReview.Rag;

/// <summary>How reference chunks are ranked, which part 6 compares (D5).</summary>
public enum ReferenceRanking
{
    /// <summary>By vector distance from the query.</summary>
    Vector,

    /// <summary>Vector distance and full-text score, fused by reciprocal rank.</summary>
    Hybrid,

    /// <summary>
    /// No search: the provider docs for the slice's types, in a fixed order, and nothing else. The baseline vectors have
    /// to beat (D5), because it's what filtering alone can do.
    /// </summary>
    Metadata,
}

/// <summary>
/// What an agent's references should be about. The text is built by code from the slice, never from a model, and holds
/// resource types and attribute names, not values.
/// </summary>
/// <param name="Text">What to search for.</param>
/// <param name="ResourceTypes">The slice's types, which provider docs are filtered on.</param>
/// <param name="ProviderVersion">The azurerm version in the repo's lock file, or null when it isn't known.</param>
/// <param name="TokenBudget">Separate from the team's rules, so references can't push a rule out (D42).</param>
/// <param name="Ranking">How results are ordered.</param>
/// <param name="Candidates">How many of each kind to consider, which a re-ranker wants more of than a budget holds.</param>
public sealed record ReferenceQuery(
    string Text,
    IReadOnlyCollection<string> ResourceTypes,
    string? ProviderVersion,
    int TokenBudget,
    ReferenceRanking Ranking = ReferenceRanking.Hybrid,
    int Candidates = 8);

/// <summary>One reference chunk, with what a prompt shows and what attribution needs.</summary>
public sealed record ReferenceDoc(
    string Id,
    string Source,
    string Title,
    string Heading,
    string Text,
    string SourceUrl,
    string License,
    string Attribution,
    IReadOnlyList<string> AppliesTo);

/// <summary>
/// The chunks found, best first, and whether provider docs were available, because a version that isn't indexed gets
/// none and the result has to say so. A prompt sorts them by ID, so the same chunks always give the same prompt.
/// </summary>
public sealed record ReferenceRetrieval(IReadOnlyList<ReferenceDoc> Docs, string? CorpusVersion, bool ProviderDocsMissing)
{
    public static readonly ReferenceRetrieval None = new([], null, false);
}

/// <summary>Imported reference material. It informs findings and can't create one (D42).</summary>
public interface IReferenceRetriever
{
    Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Reads provider versions from a lock file, because a plan's JSON doesn't name them.</summary>
public static partial class LockFile
{
    /// <summary>The azurerm version a lock file pins, from either registry, or null if there isn't one.</summary>
    public static string? AzurermVersion(string? lockFile)
    {
        if (string.IsNullOrEmpty(lockFile))
        {
            return null;
        }

        var match = AzurermBlock().Match(lockFile.ReplaceLineEndings("\n"));
        return match.Success ? match.Groups["version"].Value : null;
    }

    [GeneratedRegex("""^provider "registry\.(opentofu\.org|terraform\.io)/hashicorp/azurerm" \{\n(?:[^}\n]*\n)*?\s*version\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"$""", RegexOptions.Multiline)]
    private static partial Regex AzurermBlock();
}
