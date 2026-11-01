using TfReview.Core;

namespace TfReview.Rag;

/// <summary>What an agent wants: the topics it declares and the resource types in its slice.</summary>
public sealed record RetrievalQuery(IReadOnlyList<string> Topics, IReadOnlyCollection<string> ResourceTypes, int TokenCap = 8_000);

/// <summary>The documents an agent gets, and the IDs of those that matched but didn't fit its token cap.</summary>
public sealed record Retrieval(IReadOnlyList<StandardsDoc> Docs, IReadOnlyList<string> OmittedDocIds);

public interface IRetriever
{
    Task<Retrieval> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata filtering over whole documents, the baseline M3's vectors have to beat (D5). A document too
/// big for the cap is left out whole and reported, because half a standard could read as permission.
/// </summary>
public sealed class FileRetriever(StandardsCorpus corpus) : IRetriever
{
    public Task<Retrieval> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default)
    {
        var docs = new List<StandardsDoc>();
        var omitted = new List<string>();
        var remaining = query.TokenCap;

        // The corpus is sorted by ID, so the same query always gives the same prompt.
        foreach (var doc in corpus.Docs.Where(doc => Matches(doc, query)))
        {
            var tokens = TokenEstimate.Of(doc.Body);
            if (tokens > remaining)
            {
                omitted.Add(doc.Id);
                continue;
            }

            docs.Add(doc);
            remaining -= tokens;
        }

        return Task.FromResult(new Retrieval(docs, omitted));
    }

    private static bool Matches(StandardsDoc doc, RetrievalQuery query) =>
        doc.Status == StandardsDoc.Accepted
        && doc.Topics.Any(topic => query.Topics.Contains(topic, StringComparer.Ordinal))
        && query.ResourceTypes.Any(doc.AppliesToType);
}
