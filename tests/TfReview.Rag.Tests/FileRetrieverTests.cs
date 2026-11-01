using TfReview.Core;

namespace TfReview.Rag.Tests;

public class FileRetrieverTests
{
    private static readonly string[] ConventionsTopics = ["naming", "tagging"];

    [Test]
    public async Task Reads_frontmatter_and_separates_the_body()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001-naming.md", Doc("ADR-001", body: "# Naming\n\nThe rule."));

        var doc = (await corpus.LoadAsync()).Docs.Single();

        await Assert.That(doc.Id).IsEqualTo("ADR-001");
        await Assert.That(doc.Severity).IsEqualTo(Severity.Low);
        await Assert.That(doc.Path).IsEqualTo("adr/ADR-001-naming.md");
        await Assert.That(doc.Body).IsEqualTo("# Naming\n\nThe rule.");
    }

    [Test]
    public async Task Selects_by_topic()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", topics: "[naming]"));
        corpus.Write("policies/POL-001.md", Doc("POL-001", topics: "[security]"));

        await Assert.That(await Retrieve(corpus, ["a"])).IsEquivalentTo(["ADR-001"]);
    }

    [Test]
    public async Task Selects_by_resource_type_and_the_wildcard()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", appliesTo: "[a]"));
        corpus.Write("adr/ADR-002.md", Doc("ADR-002", appliesTo: "[\"*\"]"));
        corpus.Write("adr/ADR-003.md", Doc("ADR-003", appliesTo: "[b]"));

        await Assert.That(await Retrieve(corpus, ["a"])).IsEquivalentTo(["ADR-001", "ADR-002"]);
    }

    [Test]
    public async Task Retrieves_nothing_for_a_plan_with_no_resource_types()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", appliesTo: "[\"*\"]"));

        await Assert.That(await Retrieve(corpus, [])).IsEmpty();
    }

    // Agents must never cite a retired decision.
    [Test]
    [Arguments("proposed")]
    [Arguments("deprecated")]
    public async Task Only_retrieves_accepted_docs(string status)
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", status: status));

        await Assert.That(await Retrieve(corpus, ["a"])).IsEmpty();
    }

    [Test]
    public async Task Omits_a_doc_over_the_cap_whole_and_reports_it()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", body: new string('x', 300)));
        corpus.Write("adr/ADR-002.md", Doc("ADR-002", body: new string('x', 3000)));
        corpus.Write("adr/ADR-003.md", Doc("ADR-003", body: new string('x', 300)));

        var result = await new FileRetriever(await corpus.LoadAsync())
            .RetrieveAsync(new RetrievalQuery(ConventionsTopics, ["a"], TokenCap: 250));

        await Assert.That(result.Docs.Select(doc => doc.Id)).IsEquivalentTo(["ADR-001", "ADR-003"]);
        await Assert.That(result.OmittedDocIds).IsEquivalentTo(["ADR-002"]);
        await Assert.That(result.Docs[1].Body.Length).IsEqualTo(300);
    }

    [Test]
    public async Task Fills_the_cap_in_id_order_whatever_the_file_names()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/z.md", Doc("ADR-001", body: new string('x', 300)));
        corpus.Write("adr/a.md", Doc("ADR-002", body: new string('x', 300)));

        var result = await new FileRetriever(await corpus.LoadAsync())
            .RetrieveAsync(new RetrievalQuery(ConventionsTopics, ["a"], TokenCap: 100));

        await Assert.That(result.Docs.Single().Id).IsEqualTo("ADR-001");
        await Assert.That(result.OmittedDocIds).IsEquivalentTo(["ADR-002"]);
    }

    // A README has none, and .claude's rules have frontmatter that means something else.
    [Test]
    public async Task Ignores_markdown_that_isnt_a_corpus_doc()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/README.md", "# About these ADRs");
        corpus.Write("README.md", "# Standards");
        corpus.Write(".claude/rules/rego.md", "---\npaths: ['**/*.rego']\n---\n\nRules.");
        corpus.Write("adr/ADR-001.md", Doc("ADR-001"));

        await Assert.That((await corpus.LoadAsync()).Docs.Select(doc => doc.Id)).IsEquivalentTo(["ADR-001"]);
    }

    [Test]
    public async Task Reads_windows_line_endings()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", Doc("ADR-001", body: "Text.").ReplaceLineEndings("\r\n"));

        await Assert.That((await corpus.LoadAsync()).Docs.Single().Body).IsEqualTo("Text.");
    }

    // Skipping these would quietly review against fewer standards than the repo asked for.
    [Test]
    [Arguments("---\nid: ADR-001\nno closing fence")]
    [Arguments("---\n: [broken\n---\n\nText.")]
    [Arguments("---\nid: ADR-001\nsurprise: true\n---\n\nText.")]
    [Arguments("---\nid: nope\nstatus: accepted\napplies_to: [a]\ntopics: [naming]\nseverity: low\n---\n")]
    [Arguments("---\nid: ADR-001\nstatus: odd\napplies_to: [a]\ntopics: [naming]\nseverity: low\n---\n")]
    [Arguments("---\nid: ADR-001\nstatus: accepted\napplies_to: []\ntopics: [naming]\nseverity: low\n---\n")]
    [Arguments("---\nid: ADR-001\nstatus: accepted\napplies_to: [a]\ntopics: [odd]\nseverity: low\n---\n")]
    [Arguments("---\nid: ADR-001\nstatus: accepted\napplies_to: [a]\ntopics: [naming]\nseverity: odd\n---\n")]
    [Arguments("---\n---\n")]
    public async Task Refuses_a_broken_doc(string text)
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/ADR-001.md", text);

        await Assert.That(async () => { await corpus.LoadAsync(); }).Throws<CorpusException>();
    }

    [Test]
    public async Task Refuses_two_docs_with_one_id()
    {
        using var corpus = new TempCorpus();
        corpus.Write("adr/a.md", Doc("ADR-001"));
        corpus.Write("adr/b.md", Doc("ADR-001"));

        await Assert.That(async () => { await corpus.LoadAsync(); }).Throws<CorpusException>();
    }

    [Test]
    public async Task An_empty_corpus_has_no_docs()
    {
        using var corpus = new TempCorpus();

        await Assert.That((await corpus.LoadAsync()).Docs).IsEmpty();
    }

    /// <summary>The real corpus, from TF_STANDARDS_DIR. CI always provides it; locally this skips if it isn't set.</summary>
    [Test]
    public async Task Gives_the_conventions_agent_the_real_naming_and_tagging_docs()
    {
        if (Environment.GetEnvironmentVariable("TF_STANDARDS_DIR") is not { } directory)
        {
            Skip.Test("TF_STANDARDS_DIR isn't set.");
            return;
        }

        var retriever = new FileRetriever(await StandardsCorpus.LoadAsync(directory));

        var storage = await retriever.RetrieveAsync(new RetrievalQuery(ConventionsTopics, ["azurerm_storage_account"]));
        var subnet = await retriever.RetrieveAsync(new RetrievalQuery(ConventionsTopics, ["azurerm_subnet"]));

        await Assert.That(storage.Docs.Select(doc => doc.Id)).IsEquivalentTo(["ADR-001", "ADR-002"]);
        await Assert.That(storage.OmittedDocIds).IsEmpty();
        await Assert.That(subnet.Docs.Select(doc => doc.Id)).IsEquivalentTo(["ADR-002"]);
    }

    [Test]
    public async Task The_local_source_copies_only_docs_and_policies()
    {
        using var source = new TempCorpus();
        using var target = new TempCorpus();
        source.Write("adr/ADR-001.md", Doc("ADR-001"));
        source.Write("adr/ADR-001.rego", "package adr001");
        source.Write("scripts/run.sh", "echo no");

        var config = new TfReviewConfig("a/b", new string('0', 40), ["conventions"], false);
        await new LocalDirectorySource(source.Path).FetchAsync(config, target.Path);

        await Assert.That(File.Exists(System.IO.Path.Combine(target.Path, "adr/ADR-001.md"))).IsTrue();
        await Assert.That(File.Exists(System.IO.Path.Combine(target.Path, "adr/ADR-001.rego"))).IsTrue();
        await Assert.That(File.Exists(System.IO.Path.Combine(target.Path, "scripts/run.sh"))).IsFalse();
    }

    [Test]
    public async Task The_local_source_reports_a_missing_directory()
    {
        using var target = new TempCorpus();
        var config = new TfReviewConfig("a/b", new string('0', 40), ["conventions"], false);

        await Assert.That(() => new LocalDirectorySource("/does/not/exist").FetchAsync(config, target.Path))
            .Throws<StandardsFetchException>();
    }

    private static async Task<IEnumerable<string>> Retrieve(TempCorpus corpus, string[] resourceTypes)
    {
        var result = await new FileRetriever(await corpus.LoadAsync())
            .RetrieveAsync(new RetrievalQuery(ConventionsTopics, resourceTypes));
        return result.Docs.Select(doc => doc.Id);
    }

    private static string Doc(
        string id, string status = "accepted", string appliesTo = "[\"*\"]", string topics = "[naming]", string body = "Text.") =>
        $"---\nid: {id}\ntype: adr\nstatus: {status}\napplies_to: {appliesTo}\ntopics: {topics}\nseverity: low\nrego: {id}.rego\n---\n\n{body}\n";

    private sealed class TempCorpus : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("tfreview-corpus-");

        public string Path => directory.FullName;

        public void Write(string relative, string text)
        {
            var path = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public Task<StandardsCorpus> LoadAsync() => StandardsCorpus.LoadAsync(Path);

        public void Dispose() => directory.Delete(recursive: true);
    }
}
