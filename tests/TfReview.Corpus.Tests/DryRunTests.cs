namespace TfReview.Corpus.Tests;

public class DryRunTests
{
    private const string OldCommit = "1111111111111111111111111111111111111111";
    private const string NewCommit = "2222222222222222222222222222222222222222";

    [Test]
    public async Task A_pin_change_reports_what_changes_and_links_to_upstreams_diff()
    {
        var before = Report(OldCommit, ("docs/a.md", "a"), ("docs/b.md", "b"), ("docs/gone.md", "gone"));
        var after = Report(NewCommit, ("docs/a.md", "a"), ("docs/b.md", "b changed"), ("docs/new.md", "new"));

        var text = DryRun.Report(null, [before], Sources(), [after]);

        await Assert.That(text).Contains("| azure-policy | 3 | 1 | 1 | 1 | 3 | 2 |");
        await Assert.That(text).Contains($"https://github.com/a/b/compare/{OldCommit}...{NewCommit}");
    }

    [Test]
    public async Task An_unchanged_pin_has_nothing_to_embed()
    {
        var same = Report(OldCommit, ("docs/a.md", "a"));

        var text = DryRun.Report(null, [same], Sources(), [same]);

        await Assert.That(text).Contains("| azure-policy | 1 | 0 | 0 | 0 | 1 | 0 | none |");
    }

    // Upstream text is untrusted, and the report is read by whoever reviews the pin change.
    [Test]
    public async Task The_report_holds_no_upstream_text()
    {
        var after = Report(NewCommit, ("docs/a.md", "CANARY upstream text"));

        var text = DryRun.Report(null, [], Sources(), [after]);

        await Assert.That(text).DoesNotContain("CANARY");
    }

    private static CorpusSources Sources() => CorpusSources.Parse($"""
        version: 1
        embedding_model: test-model
        sources:
          - id: azure-policy
            repository: a/b
            commit: {NewCommit}
            license: MIT
            attribution: Someone, MIT
            include: [docs]
        """);

    private static ImportReport Report(string commit, params (string Path, string Text)[] docs)
    {
        var pin = new SourcePin("azure-policy", "a/b", commit, "MIT", "Someone, MIT", ["docs"], null);
        var chunks = docs.Select((doc, index) => new ReferenceChunk(
            $"REF-{index:x32}", "0123456789abcdef", pin.Id, pin.Repository, commit, doc.Path, "https://github.com/a/b/blob/0/x",
            pin.License, pin.Attribution, "Title", string.Empty, [], doc.Text, ("h" + doc.Text).PadRight(64, '0'))).ToList();
        return new ImportReport(pin, docs.Length, 0, 0, chunks);
    }
}
