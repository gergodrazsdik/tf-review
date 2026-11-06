using System.Text.Json;
using System.Text.Json.Serialization;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Eval;

/// <summary>
/// Stands in for the index in replay, so the production default, references per finding through the suggester (D50),
/// runs offline. A case opts in with a references.json: the excerpts every search returns, and the sentence the
/// suggester's recorded answer should add, or null when code has to reject it. The excerpts and that answer are
/// written by hand to the schema, like a recording: replay proves the code around the model, not the model.
/// </summary>
internal sealed class ReplayReferences(ReplayReferences.Fixture fixture) : IReferenceRetriever
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ReplayReferences? Load(string directory)
    {
        var path = Path.Combine(directory, "references.json");
        return File.Exists(path) ? new ReplayReferences(JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Options)!) : null;
    }

    public Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReferenceRetrieval(fixture.Docs, fixture.CorpusVersion, false));

    /// <summary>
    /// What's wrong with a replay with references, beside the usual checks: the findings have to be the same as without
    /// them (D42), and the only change one finding's suggestion, by exactly the expected sentence or not at all.
    /// </summary>
    public IReadOnlyList<string> Problems(ReviewResult without, ReviewResult with)
    {
        var problems = new List<string>();
        if (with.References is null)
        {
            problems.Add("the result doesn't say references were used");
        }

        var before = without.Findings.Select(finding => (finding.ResourceAddress, finding.Rule, finding.Title)).ToList();
        var after = with.Findings.Select(finding => (finding.ResourceAddress, finding.Rule, finding.Title)).ToList();
        if (!before.SequenceEqual(after))
        {
            problems.Add("references changed the findings, which they mustn't (D42)");
            return problems;
        }

        var added = without.Findings.Zip(with.Findings)
            .Where(pair => pair.First.Suggestion != pair.Second.Suggestion)
            .Select(pair => (pair.Second.ResourceAddress, pair.Second.Rule, pair.Second.Suggestion))
            .ToList();
        if (fixture.Addition is null)
        {
            problems.AddRange(added.Select(item => $"{item.ResourceAddress} {item.Rule}: an addition code should have rejected: {item.Suggestion}"));
        }
        else if (added is not [var only] || only.Suggestion?.EndsWith(" " + fixture.Addition, StringComparison.Ordinal) != true)
        {
            problems.Add($"expected one suggestion to gain \"{fixture.Addition}\", and got {added.Count} changed: {string.Join(" / ", added.Select(item => item.Suggestion))}");
        }

        return problems;
    }

    internal sealed record Fixture(
        [property: JsonPropertyName("corpus_version")] string CorpusVersion,
        [property: JsonPropertyName("docs")] IReadOnlyList<ReferenceDoc> Docs,
        [property: JsonPropertyName("addition")] string? Addition);
}
