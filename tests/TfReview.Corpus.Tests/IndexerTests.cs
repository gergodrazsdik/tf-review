namespace TfReview.Corpus.Tests;

public class IndexerTests
{
    private const string Old = "1111111111111111";
    private const string New = "2222222222222222";

    [Test]
    public async Task A_first_run_embeds_everything_and_switches_once_its_stored()
    {
        var store = new FakeStore();
        var embedder = new FakeEmbedder();

        var outcome = await new Indexer(store, embedder, _ => { }).RunAsync(Chunks("a", "b", "c"), New);

        await Assert.That(outcome.Embedded).IsEqualTo(3);
        await Assert.That(outcome.Switched).IsTrue();
        await Assert.That(store.Active).IsEqualTo(New);
        await Assert.That(store.Versions[New].Count).IsEqualTo(3);
    }

    [Test]
    public async Task Only_changed_chunks_cost_an_embedding()
    {
        var store = new FakeStore();
        await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a", "b", "c"), Old);
        var embedder = new FakeEmbedder();

        var outcome = await new Indexer(store, embedder, _ => { }).RunAsync(Chunks("a", "b", "changed"), New);

        await Assert.That(outcome.Embedded).IsEqualTo(1);
        await Assert.That(outcome.Reused).IsEqualTo(2);
        await Assert.That(embedder.Texts.Single()).Contains("changed");
    }

    [Test]
    public async Task The_old_version_goes_once_the_new_one_is_in_use()
    {
        var store = new FakeStore();
        await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a"), Old);

        var outcome = await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a"), New);

        await Assert.That(outcome.Deleted).IsEquivalentTo([Old]);
        await Assert.That(store.Versions.ContainsKey(Old)).IsFalse();
    }

    // D44: a review must never see a half-built index.
    [Test]
    public async Task A_failed_embedding_leaves_the_old_version_in_use()
    {
        var store = new FakeStore();
        await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a"), Old);
        var failing = new FakeEmbedder { FailAfter = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Indexer(store, failing, _ => { }, batchSize: 1).RunAsync(Chunks("x", "y", "z"), New));

        await Assert.That(store.Active).IsEqualTo(Old);
        await Assert.That(store.Versions.ContainsKey(Old)).IsTrue();
    }

    [Test]
    public async Task A_version_that_didnt_store_everything_isnt_switched_to()
    {
        var store = new FakeStore { DropWrites = 1 };

        await Assert.ThrowsAsync<IndexIncompleteException>(() =>
            new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a", "b"), New));

        await Assert.That(store.Active).IsNull();
    }

    [Test]
    public async Task A_rerun_after_a_failure_reuses_what_it_got_as_far_as_storing()
    {
        var store = new FakeStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Indexer(store, new FakeEmbedder { FailAfter = 1 }, _ => { }, batchSize: 1).RunAsync(Chunks("x", "y"), New));
        var embedder = new FakeEmbedder();

        var outcome = await new Indexer(store, embedder, _ => { }, batchSize: 1).RunAsync(Chunks("x", "y"), New);

        await Assert.That(outcome.Embedded).IsEqualTo(1);
        await Assert.That(store.Active).IsEqualTo(New);
    }

    [Test]
    public async Task A_complete_version_in_use_costs_nothing_to_run_again()
    {
        var store = new FakeStore();
        await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a"), New);
        var embedder = new FakeEmbedder();
        var writes = store.Writes;

        var outcome = await new Indexer(store, embedder, _ => { }).RunAsync(Chunks("a"), New);

        await Assert.That(outcome.Switched).IsFalse();
        await Assert.That(embedder.Texts).IsEmpty();
        await Assert.That(store.Writes).IsEqualTo(writes);
    }

    [Test]
    public async Task A_rebuild_embeds_everything_again()
    {
        var store = new FakeStore();
        await new Indexer(store, new FakeEmbedder(), _ => { }).RunAsync(Chunks("a", "b"), New);
        var embedder = new FakeEmbedder();

        var outcome = await new Indexer(store, embedder, _ => { }).RunAsync(Chunks("a", "b"), New, rebuild: true);

        await Assert.That(outcome.Embedded).IsEqualTo(2);
    }

    [Test]
    public async Task Each_chunk_is_written_once_per_run()
    {
        var store = new FakeStore();

        await new Indexer(store, new FakeEmbedder(), _ => { }, batchSize: 2).RunAsync(Chunks("a", "b", "c"), New);

        await Assert.That(store.Writes).IsEqualTo(3);
    }

    [Test]
    public async Task Chunks_with_the_same_text_share_one_embedding()
    {
        var store = new FakeStore();
        var embedder = new FakeEmbedder();
        var chunks = Chunks("same", "same");

        await new Indexer(store, embedder, _ => { }).RunAsync(chunks, New);

        await Assert.That(embedder.Texts.Count).IsEqualTo(1);
        await Assert.That(store.Versions[New].Count).IsEqualTo(2);
    }

    private static List<ReferenceChunk> Chunks(params string[] texts) =>
    [
        .. texts.Select((text, index) => new ReferenceChunk(
            $"REF-{index:x32}",
            New,
            "azurerm-docs",
            "a/b",
            new string('0', 40),
            $"docs/{index}.md",
            "https://github.com/a/b/blob/0/docs",
            "MPL-2.0",
            "Someone",
            "Title",
            string.Empty,
            [],
            text,
            ("hash-" + text).PadRight(64, '0'))),
    ];

    private sealed class FakeEmbedder : IEmbedder
    {
        public List<string> Texts { get; } = [];

        public int? FailAfter { get; init; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            if (this.FailAfter is { } limit && this.Texts.Count >= limit)
            {
                throw new InvalidOperationException("The model is away.");
            }

            this.Texts.AddRange(texts);
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(text => new[] { (float)text.Length })]);
        }
    }

    private sealed class FakeStore : IReferenceStore
    {
        public Dictionary<string, Dictionary<string, StoredChunk>> Versions { get; } = [];

        public string? Active { get; private set; }

        public int Writes { get; private set; }

        public int DropWrites { get; set; }

        public Task<string?> ReadActiveVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(this.Active);

        public Task<IReadOnlyDictionary<string, float[]>> ReadEmbeddingsAsync(string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, float[]>>(
                this.Versions.TryGetValue(version, out var chunks)
                    ? chunks.Values.DistinctBy(stored => stored.Chunk.ContentHash).ToDictionary(stored => stored.Chunk.ContentHash, stored => stored.Embedding)
                    : []);

        public Task UpsertAsync(string version, IReadOnlyList<StoredChunk> chunks, CancellationToken cancellationToken = default)
        {
            var stored = this.Versions.TryGetValue(version, out var existing) ? existing : this.Versions[version] = [];
            foreach (var chunk in chunks)
            {
                this.Writes++;
                if (this.DropWrites > 0)
                {
                    this.DropWrites--;
                    continue;
                }

                stored[chunk.Chunk.Id] = chunk;
            }

            return Task.CompletedTask;
        }

        public Task<int> CountAsync(string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Versions.TryGetValue(version, out var chunks) ? chunks.Count : 0);

        public Task SetActiveVersionAsync(string version, CancellationToken cancellationToken = default)
        {
            this.Active = version;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. this.Versions.Keys]);

        public Task DeleteVersionAsync(string version, CancellationToken cancellationToken = default)
        {
            this.Versions.Remove(version);
            return Task.CompletedTask;
        }
    }
}
