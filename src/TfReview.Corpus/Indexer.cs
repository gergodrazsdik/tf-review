namespace TfReview.Corpus;

public sealed class IndexIncompleteException(string message) : Exception(message);

/// <summary>A chunk as stored: its fields and its vector.</summary>
public sealed record StoredChunk(ReferenceChunk Chunk, float[] Embedding);

/// <summary>Where the index lives. One partition per corpus version, and a record of which version reviews read.</summary>
public interface IReferenceStore
{
    Task<string?> ReadActiveVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Vectors already stored in a version, by content hash.</summary>
    Task<IReadOnlyDictionary<string, float[]>> ReadEmbeddingsAsync(string version, CancellationToken cancellationToken = default);

    Task UpsertAsync(string version, IReadOnlyList<StoredChunk> chunks, CancellationToken cancellationToken = default);

    Task<int> CountAsync(string version, CancellationToken cancellationToken = default);

    Task SetActiveVersionAsync(string version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task DeleteVersionAsync(string version, CancellationToken cancellationToken = default);
}

public interface IEmbedder
{
    /// <summary>One vector per text, in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>What a run did.</summary>
public sealed record IndexOutcome(string Version, int Chunks, int Reused, int Embedded, bool Switched, IReadOnlyList<string> Deleted);

/// <summary>
/// Builds a corpus version beside the one in use, and only switches reviews to it once every chunk is stored (D44). A
/// failed run leaves the old version in use, and running again picks up what's stored. Vectors are reused by content
/// hash, which covers the text, embedding model and chunker, so only what changed costs anything.
/// </summary>
public sealed class Indexer(IReferenceStore store, IEmbedder embedder, Action<string> log, int batchSize = 64)
{
    /// <param name="chunks">Every chunk the version should hold.</param>
    /// <param name="version">The corpus version the sources name.</param>
    /// <param name="rebuild">Embed everything again, for recovering from a bad state.</param>
    /// <param name="cancellationToken">Stops the run, leaving the version in use as it was.</param>
    public async Task<IndexOutcome> RunAsync(
        IReadOnlyList<ReferenceChunk> chunks, string version, bool rebuild = false, CancellationToken cancellationToken = default)
    {
        var active = await store.ReadActiveVersionAsync(cancellationToken);
        if (active == version && !rebuild && await store.CountAsync(version, cancellationToken) == chunks.Count)
        {
            log($"Version {version} is already complete and in use.");
            return new IndexOutcome(version, chunks.Count, chunks.Count, 0, false, []);
        }

        // What the version in use has, and what an earlier run of this version got as far as storing.
        var known = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (!rebuild)
        {
            foreach (var source in new[] { active, version }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                foreach (var (hash, vector) in await store.ReadEmbeddingsAsync(source, cancellationToken))
                {
                    known.TryAdd(hash, vector);
                }
            }
        }

        var byHash = chunks.GroupBy(chunk => chunk.ContentHash, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var missing = byHash.Keys.Where(hash => !known.ContainsKey(hash)).Select(hash => byHash[hash][0]).ToList();
        var written = new HashSet<string>(StringComparer.Ordinal);
        log($"{chunks.Count} chunks: {chunks.Count - missing.Count} with a vector to reuse, {missing.Count} to embed.");

        for (var start = 0; start < missing.Count; start += batchSize)
        {
            var batch = missing.Skip(start).Take(batchSize).ToList();
            var vectors = await embedder.EmbedAsync([.. batch.Select(chunk => chunk.ToEmbed)], cancellationToken);
            if (vectors.Count != batch.Count)
            {
                throw new IndexIncompleteException("The embedding model returned the wrong number of vectors.");
            }

            for (var index = 0; index < batch.Count; index++)
            {
                known[batch[index].ContentHash] = vectors[index];
            }

            // Stored as it goes, so a run that fails later doesn't pay for these again.
            await store.UpsertAsync(version, [.. batch.SelectMany(done => byHash[done.ContentHash]).Select(chunk => new StoredChunk(chunk, known[chunk.ContentHash]))], cancellationToken);
            written.UnionWith(batch.Select(chunk => chunk.ContentHash));
            log($"Embedded {Math.Min(start + batchSize, missing.Count)} of {missing.Count}.");
        }

        // The rest reuse a vector, and each write costs request units, so nothing is written twice.
        await store.UpsertAsync(version, [.. chunks.Where(chunk => !written.Contains(chunk.ContentHash)).Select(chunk => new StoredChunk(chunk, known[chunk.ContentHash]))], cancellationToken);

        var stored = await store.CountAsync(version, cancellationToken);
        if (stored != chunks.Count)
        {
            throw new IndexIncompleteException($"Version {version} holds {stored} chunks, not {chunks.Count}, so reviews stay on {active ?? "no version"}.");
        }

        await store.SetActiveVersionAsync(version, cancellationToken);
        log($"Reviews now read version {version}.");

        var deleted = new List<string>();
        foreach (var old in await store.ListVersionsAsync(cancellationToken))
        {
            if (old != version)
            {
                await store.DeleteVersionAsync(old, cancellationToken);
                deleted.Add(old);
            }
        }

        return new IndexOutcome(version, chunks.Count, chunks.Count - missing.Count, missing.Count, true, deleted);
    }
}
