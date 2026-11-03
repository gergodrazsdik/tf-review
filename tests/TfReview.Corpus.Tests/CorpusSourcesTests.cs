using System.Runtime.CompilerServices;

namespace TfReview.Corpus.Tests;

public class CorpusSourcesTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static string Source(string license = "MIT", string commit = Commit, string repository = "a/b", string include = "docs", string id = "azure-policy") => $"""
        version: 1
        embedding_model: test-model
        sources:
          - id: {id}
            repository: {repository}
            commit: {commit}
            license: {license}
            attribution: Someone, MIT
            include: [{include}]
        """;

    private static string Provider(string version = "5.12.0") => $"""
        version: 1
        embedding_model: test-model
        sources:
          - id: azurerm-docs
            repository: a/b
            license: MPL-2.0
            attribution: Someone, MPL-2.0
            include: [website/docs/r]
            versions:
              - version: {version}
                commit: {Commit}
              - version: 5.13.0
                commit: {Commit}
        """;

    [Test]
    public async Task The_real_sources_file_is_valid_and_pins_every_source()
    {
        var sources = CorpusSources.Parse(await File.ReadAllTextAsync(SourcesPath()));

        await Assert.That(sources.Pins.Select(pin => pin.Id).Distinct()).IsEquivalentTo(["azure-policy", "cloud-adoption-framework", "azurerm-docs"]);
        await Assert.That(sources.EmbeddingModel).IsEqualTo("text-embedding-3-small");
    }

    [Test]
    public async Task A_provider_gets_a_pin_per_version()
    {
        var sources = CorpusSources.Parse(Provider());

        await Assert.That(sources.Pins.Select(pin => pin.ProviderVersion!)).IsEquivalentTo(["5.12.0", "5.13.0"]);
    }

    [Test]
    [Arguments("CC-BY-NC-4.0")]
    [Arguments("proprietary")]
    [Arguments("mit")]
    public async Task A_licence_not_on_the_list_is_refused(string license)
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source(license: license))));
    }

    [Test]
    [Arguments("main")]
    [Arguments("v5.12.0")]
    [Arguments("0123456")]
    public async Task A_source_has_to_pin_a_full_commit(string commit)
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source(commit: commit))));
    }

    [Test]
    [Arguments("../etc")]
    [Arguments("docs/../../etc")]
    [Arguments("/etc")]
    public async Task An_include_outside_the_repo_is_refused(string include)
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source(include: include))));
    }

    [Test]
    public async Task An_exclude_outside_the_repo_is_refused()
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source() + "\n    exclude: [../etc]")));
    }

    [Test]
    public async Task A_source_with_no_importer_is_refused()
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source(id: "cis-benchmarks"))));
    }

    [Test]
    public async Task An_unknown_field_is_refused()
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source() + "\nextra: true")));
    }

    [Test]
    public async Task A_provider_without_versions_is_refused()
    {
        await Assert.ThrowsAsync<SourcesException>(() => Task.FromResult(CorpusSources.Parse(Source(id: "azurerm-docs"))));
    }

    [Test]
    public async Task The_corpus_version_follows_the_pins_and_nothing_else()
    {
        var first = CorpusSources.Parse(Source()).CorpusVersion;

        await Assert.That(CorpusSources.Parse(Source()).CorpusVersion).IsEqualTo(first);
        await Assert.That(CorpusSources.Parse("# a comment\n" + Source()).CorpusVersion).IsEqualTo(first);
        await Assert.That(CorpusSources.Parse(Source(commit: "1123456789abcdef0123456789abcdef01234567")).CorpusVersion).IsNotEqualTo(first);
        await Assert.That(CorpusSources.Parse(Source(include: "other")).CorpusVersion).IsNotEqualTo(first);
        await Assert.That(CorpusSources.Parse(Source() + "\n    exclude: [docs/old]").CorpusVersion).IsNotEqualTo(first);
        await Assert.That(first).Matches("^[0-9a-f]{16}$");
    }

    private static string SourcesPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../corpus/sources.yml"));
}
