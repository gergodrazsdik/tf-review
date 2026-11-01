using TfReview.Core;

namespace TfReview.GitHub.Tests;

public class CommentRendererTests
{
    private const string LinkCommit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string Commit = new('a', 40);

    private static readonly PlanCounts Counts = new(
        3, new Dictionary<string, int> { ["create"] = 2, ["delete"] = 1, ["no-op"] = 4, ["update"] = 0 });

    [Test]
    public async Task Starts_with_the_marker_the_sticky_comment_looks_for()
    {
        await Assert.That(CommentRenderer.Render(NotReviewedResult(), Counts)).StartsWith(CommentRenderer.Marker);
    }

    [Test]
    public async Task Says_when_nothing_was_reviewed_and_why()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), Counts);

        await Assert.That(comment).Contains("**Not reviewed** for commit `aaaaaaa`");
        await Assert.That(comment).Contains("Not reviewed: conventions (unavailable).");
    }

    [Test]
    public async Task Summarizes_the_plan_without_zero_counts()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), Counts);

        await Assert.That(comment).Contains("Plan: 3 resource change(s) (2 create, 1 delete, 4 no-op).");
    }

    [Test]
    public async Task Still_renders_when_the_counts_are_missing()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), null);

        await Assert.That(comment).DoesNotContain("Plan:");
        await Assert.That(comment).Contains("**Not reviewed**");
    }

    [Test]
    public async Task Never_presents_no_findings_as_approval()
    {
        var comment = CommentRenderer.Render(Result(Verdict.NoFindings), Counts);

        await Assert.That(comment).Contains("**No findings**");
        await Assert.That(comment).Contains("finding nothing doesn't mean a change is approved");
    }

    [Test]
    public async Task Shows_findings_in_a_table()
    {
        var comment = CommentRenderer.Render(Result(Verdict.Findings, Finding("Required tag owner is missing")), Counts);

        await Assert.That(comment).Contains("**1 finding(s)**");
        await Assert.That(comment).Contains("| medium | `azurerm_x.y` | `ADR-002` | `Required tag owner is missing` |  | policy |");
    }

    [Test]
    public async Task Links_a_cited_doc_at_the_pinned_commit()
    {
        var comment = CommentRenderer.Render(Linked("adr/ADR-002-tags.md"), Counts);

        await Assert.That(comment).Contains($"[ADR-002](https://github.com/owner/standards/blob/{LinkCommit}/adr/ADR-002-tags.md)");
    }

    // The result file passes between jobs, so a bad value mustn't become a link.
    [Test]
    [Arguments("../../evil")]
    [Arguments("adr/x.md) [click](https://evil.example")]
    [Arguments("https://evil.example/x")]
    public async Task Doesnt_link_a_path_that_looks_wrong(string path)
    {
        var comment = CommentRenderer.Render(Linked(path), Counts);

        await Assert.That(comment).DoesNotContain("](https://");
        await Assert.That(comment).Contains("| `ADR-002` |");
    }

    [Test]
    public async Task Doesnt_link_a_rule_that_isnt_a_doc_id()
    {
        var result = Linked("adr/x.md") with { DocPaths = new Dictionary<string, string> { ["CKV_AZURE_1"] = "adr/x.md" } };
        result = result with { Findings = [result.Findings[0] with { Rule = "CKV_AZURE_1" }] };

        await Assert.That(CommentRenderer.Render(result, Counts)).DoesNotContain("](https://");
    }

    [Test]
    public async Task Shows_a_suggestion_as_inert_text()
    {
        var result = Linked("adr/x.md");
        result = result with { Findings = [result.Findings[0] with { Suggestion = "See [this](https://evil.example)" }] };

        await Assert.That(CommentRenderer.Render(result, Counts)).Contains("| `See [this](https://evil.example)` |");
    }

    [Test]
    public async Task Says_which_docs_didnt_fit()
    {
        var result = Linked("adr/x.md") with { OmittedDocs = ["ADR-003"] };

        await Assert.That(CommentRenderer.Render(result, Counts)).Contains("Not checked, too large for the prompt: `ADR-003`.");
    }

    [Test]
    public async Task Labels_a_finding_both_found()
    {
        var result = Linked("adr/x.md");
        result = result with { Findings = [result.Findings[0] with { Deterministic = true, Agent = Reporter.Conventions }] };

        await Assert.That(CommentRenderer.Render(result, Counts)).Contains("| policy and model |");
    }

    [Test]
    public async Task Labels_a_finding_no_scanner_backs_as_coming_from_a_model()
    {
        var finding = Finding("x") with { Deterministic = false, Agent = Reporter.Conventions };

        await Assert.That(CommentRenderer.Render(Result(Verdict.Findings, finding), Counts)).Contains("| model |");
    }

    [Test]
    public async Task Limits_how_many_findings_it_shows()
    {
        var findings = Enumerable.Range(0, 60).Select(i => Finding($"finding {i}")).ToArray();

        var comment = CommentRenderer.Render(Result(Verdict.Findings, findings), Counts);

        await Assert.That(comment).Contains("`finding 49`");
        await Assert.That(comment).DoesNotContain("`finding 50`");
        await Assert.That(comment).Contains("10 more finding(s) aren't shown.");
    }

    // A resource name comes from the PR, and it's part of ADR-001's message.
    [Test]
    [Arguments("![tracker](https://evil.example/x.png)")]
    [Arguments("[click](https://evil.example)")]
    [Arguments("<img src=x onerror=alert(1)>")]
    [Arguments("@someone please approve")]
    [Arguments("<!-- tf-review -->")]
    [Arguments("https://evil.example")]
    public async Task Keeps_hostile_text_inside_one_code_span(string text)
    {
        var code = CommentRenderer.Code(text);

        await Assert.That(code).IsEqualTo($"`{text}`");
    }

    [Test]
    public async Task Cant_be_closed_early_with_backticks()
    {
        await Assert.That(CommentRenderer.Code("a`b``c")).IsEqualTo("```a`b``c```");
    }

    [Test]
    public async Task Pads_text_that_starts_or_ends_with_a_backtick()
    {
        await Assert.That(CommentRenderer.Code("`a`")).IsEqualTo("`` `a` ``");
    }

    [Test]
    public async Task Cant_end_a_table_cell_with_a_pipe()
    {
        await Assert.That(CommentRenderer.Code("a|b")).IsEqualTo("`a\\|b`");
    }

    [Test]
    [Arguments("line one\nline two")]
    [Arguments("line one\r\n| new | row |")]
    [Arguments("a\u2028b")]
    [Arguments("a\u202Eb")]
    [Arguments("a\u200Bb")]
    public async Task Cant_break_out_of_a_row_or_disguise_text(string text)
    {
        var code = CommentRenderer.Code(text);

        foreach (var character in new[] { "\n", "\r", "\u2028", "\u202E", "\u200B" })
        {
            await Assert.That(code).DoesNotContain(character);
        }
    }

    [Test]
    public async Task Limits_the_length_of_untrusted_text()
    {
        var code = CommentRenderer.Code(new string('a', 5000));

        await Assert.That(code.Length).IsLessThan(310);
        await Assert.That(code).Contains("…");
    }

    [Test]
    public async Task Still_renders_empty_text()
    {
        await Assert.That(CommentRenderer.Code("  ")).IsEqualTo("` `");
    }

    [Test]
    public async Task Doesnt_let_a_hostile_address_or_rule_out_of_the_table()
    {
        var finding = Finding("x") with { ResourceAddress = "a.b\n# heading", Rule = "![x](y)" };

        var comment = CommentRenderer.Render(Result(Verdict.Findings, finding), Counts);

        await Assert.That(comment).Contains("`a.b # heading`");
        await Assert.That(comment).Contains("`![x](y)`");
    }

    [Test]
    [Arguments(NotReviewedReason.InvalidConfig, "the config is invalid")]
    [Arguments(NotReviewedReason.StandardsUnavailable, "the standards couldn't be fetched")]
    [Arguments(NotReviewedReason.ScannerFailed, "a policy check failed to run")]
    [Arguments(NotReviewedReason.RedactionRefused, "redaction refused the plan")]
    public async Task Explains_why_a_review_failed(NotReviewedReason reason, string text)
    {
        var comment = CommentRenderer.Render(ReviewResult.Failed(Commit, ReviewScope.Plan, reason), null);

        await Assert.That(comment).Contains($"Not reviewed: the whole plan ({text}).");
    }

    private static Finding Finding(string title) => new(
        "azurerm_x.y", "ADR-002", Severity.Medium, Reporter.Scanner, true, title,
        [new PlanEvidence("/resource_changes/azurerm_x.y/change/after"), new DocEvidence("ADR-002")]);

    private static ReviewResult Result(Verdict verdict, params Finding[] findings) =>
        new(1, Commit, verdict, findings, [], new Dictionary<string, int>());

    private static ReviewResult NotReviewedResult() =>
        ReviewResult.Failed(Commit, ReviewScope.Conventions, NotReviewedReason.Unavailable);

    private static ReviewResult Linked(string path) =>
        new(
            1,
            Commit,
            Verdict.Findings,
            [new Finding("a.b", "ADR-002", Severity.Medium, Reporter.Conventions, false, "t", [new PlanEvidence("/resource_changes/a.b")])],
            [],
            new Dictionary<string, int>())
        {
            StandardsCommit = LinkCommit,
            StandardsRepository = "owner/standards",
            DocPaths = new Dictionary<string, string> { ["ADR-002"] = path },
        };
}
