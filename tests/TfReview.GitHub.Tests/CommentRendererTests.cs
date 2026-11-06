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
    public async Task Marks_what_blocks_and_says_what_the_gate_does()
    {
        var blocking = new Finding("azurerm_network_security_rule.a", "POL-002", Severity.High, Reporter.Security, true, "SSH", [new DocEvidence("POL-002")]) { Blocking = true };
        var result = new ReviewResult(1, Commit, Verdict.Findings, [blocking], [], new Dictionary<string, int>()) { Gate = GateMode.Blocking };

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains("| high, **blocking** | `azurerm_network_security_rule.a` |");
        await Assert.That(comment).Contains("This repo blocks merges only on high findings a policy or scanner reported itself");
        await Assert.That(comment).DoesNotContain("This review is advisory");
    }

    // A large plan's batches can stop short, and the comment names what they didn't reach, up to a point.
    [Test]
    public async Task Names_the_resources_a_scope_left_out()
    {
        var skipped = Enumerable.Range(0, 12).Select(index => $"azurerm_subnet.s{index:00}").ToList();
        var result = new ReviewResult(1, Commit, Verdict.NoFindings, [], [new NotReviewed(ReviewScope.Security, NotReviewedReason.PlanTooLarge) { Resources = skipped }], new Dictionary<string, int>());

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains(
            "Not reviewed by security (the plan is too large): `azurerm_subnet.s00`, `azurerm_subnet.s01`, `azurerm_subnet.s02`, `azurerm_subnet.s03`, "
            + "`azurerm_subnet.s04`, `azurerm_subnet.s05`, `azurerm_subnet.s06`, `azurerm_subnet.s07`, `azurerm_subnet.s08`, `azurerm_subnet.s09` and 2 more.");
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
    public async Task Shows_the_monthly_change()
    {
        var comment = CommentRenderer.Render(WithCost(new CostSummary(31.904m, 1141.696m, [])), Counts);

        await Assert.That(comment).Contains("Monthly cost: **+$1,109.79** (from $31.90 to $1,141.70)");
    }

    [Test]
    public async Task Shows_a_saving()
    {
        await Assert.That(CommentRenderer.Render(WithCost(new CostSummary(10m, 4m, [])), Counts)).Contains("**-$6.00**");
    }

    [Test]
    public async Task Says_when_cost_doesnt_change()
    {
        await Assert.That(CommentRenderer.Render(WithCost(new CostSummary(10m, 10m, [])), Counts)).Contains("Monthly cost: no change");
    }

    [Test]
    public async Task Says_nothing_about_cost_when_it_didnt_run()
    {
        await Assert.That(CommentRenderer.Render(NotReviewedResult(), Counts)).DoesNotContain("Monthly cost");
    }

    // An address comes from the PR, and an unknown price mustn't read as free.
    [Test]
    public async Task Names_what_couldnt_be_priced_as_inert_text()
    {
        var cost = new CostSummary(0m, 0m, [new ResourceCostChange("a.b[\"[x](https://evil.example)\"]", 0m, 0m, false)]);

        var comment = CommentRenderer.Render(WithCost(cost), Counts);

        await Assert.That(comment).Contains("Couldn't be priced, so not counted: `a.b[\"[x](https://evil.example)\"]`.");
    }

    [Test]
    public async Task Limits_the_list_of_what_couldnt_be_priced()
    {
        var resources = Enumerable.Range(0, 12).Select(i => new ResourceCostChange($"a.r{i}", 0m, 0m, false)).ToList();

        var comment = CommentRenderer.Render(WithCost(new CostSummary(0m, 0m, resources)), Counts);

        await Assert.That(comment).Contains(" and 2 more.");
        await Assert.That(comment).DoesNotContain("a.r11");
    }

    [Test]
    public async Task Says_what_to_look_at_first_as_inert_text()
    {
        var result = Linked("adr/x.md") with
        {
            RiskOrder = [new RiskItem("a.b") { Reason = "See [this](https://evil.example)" }, new RiskItem("a.c")],
        };

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains("Look at first:");
        await Assert.That(comment).Contains("- `a.b`: `See [this](https://evil.example)`");
        await Assert.That(comment).DoesNotContain("`a.c`");
    }

    [Test]
    public async Task Says_nothing_about_risk_when_nothing_deserves_a_look()
    {
        var result = Linked("adr/x.md") with { RiskOrder = [new RiskItem("a.c")] };

        await Assert.That(CommentRenderer.Render(result, Counts)).DoesNotContain("Look at first");
    }

    [Test]
    public async Task Limits_what_to_look_at_first()
    {
        var result = Linked("adr/x.md") with { RiskOrder = [.. Enumerable.Range(0, 8).Select(i => new RiskItem($"a.r{i}") { Reason = "Why." })] };

        await Assert.That(CommentRenderer.Render(result, Counts)).DoesNotContain("a.r5");
    }

    [Test]
    public async Task A_merged_row_names_its_other_rules_as_inert_text()
    {
        var result = Linked("adr/x.md");
        result = result with { Findings = [result.Findings[0] with { RelatedRules = ["CKV_AZURE_190", "[x](https://evil.example)"] }] };

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains(", also `CKV_AZURE_190`, `[x](https://evil.example)`");
    }

    [Test]
    public async Task A_scanner_rule_is_labelled_as_a_scanner()
    {
        var result = Linked("adr/x.md");
        result = result with { Findings = [result.Findings[0] with { Rule = "CKV_AZURE_190", Deterministic = true, Agent = Reporter.Scanner }] };

        await Assert.That(CommentRenderer.Render(result, Counts)).Contains("| scanner |");
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

    // D41: CC-BY asks for credit wherever its text, or a suggestion drawn from it, is shown.
    [Test]
    public async Task Credits_the_reference_material_and_says_when_provider_docs_were_missing()
    {
        var result = Linked("adr/x.md") with
        {
            References = new ReferenceUse("0123456789abcdef", true, ["HashiCorp, MPL-2.0", "Microsoft, CC-BY-4.0"]),
        };

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains("Reference material: HashiCorp, MPL-2.0; Microsoft, CC-BY-4.0.");
        await Assert.That(comment).Contains("No azurerm docs were used");
    }

    [Test]
    public async Task Says_nothing_about_references_when_none_were_asked_for()
    {
        var comment = CommentRenderer.Render(Linked("adr/x.md"), Counts);

        await Assert.That(comment).DoesNotContain("Reference material").And.DoesNotContain("azurerm docs");
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
        await Assert.That(CommentRenderer.Code("a|b", inTable: true)).IsEqualTo("`a\\|b`");
    }

    // BUG-22: outside a table GFM shows the backslash, so x["a|b"] would read as x["a\|b"].
    [Test]
    public async Task Leaves_a_pipe_alone_outside_a_table()
    {
        await Assert.That(CommentRenderer.Code("a|b")).IsEqualTo("`a|b`");
    }

    [Test]
    public async Task Escapes_a_pipe_in_the_findings_table_but_not_in_the_dismiss_list()
    {
        var finding = Finding("x") with { ResourceAddress = "x[\"a|b\"]" };

        var comment = CommentRenderer.Render(Result(Verdict.Findings, finding), Counts);

        await Assert.That(comment).Contains("| `x[\"a\\|b\"]` |");
        await Assert.That(comment).Contains("Dismiss `x[\"a|b\"]`");
    }

    // MNT-28: nothing was cut, so there's no ellipsis.
    [Test]
    public async Task Text_of_exactly_the_limit_gets_no_ellipsis()
    {
        await Assert.That(CommentRenderer.Code(new string('a', 300))).DoesNotContain("…");
    }

    // SEC-11: GitHub refuses a comment over 65,536 characters, which used to end the review as "not reviewed".
    [Test]
    public async Task A_comment_too_long_for_github_drops_rows_and_says_so()
    {
        var findings = Enumerable.Range(0, 60)
            .Select(index => Finding(new string('t', 400)) with
            {
                ResourceAddress = $"module.m[\"{index}{new string('k', 400)}\"].azurerm_storage_account.a",
                Suggestion = new string('s', 400),
            })
            .ToArray();

        var comment = CommentRenderer.Render(Result(Verdict.Findings, findings), Counts);

        await Assert.That(comment.Length).IsLessThanOrEqualTo(CommentRenderer.MaxLength);
        await Assert.That(comment).Contains("to keep this comment under GitHub's size limit.");
        await Assert.That(comment).Contains("**60 finding(s)**");
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
    [Arguments(NotReviewedReason.ScannerFailed, "a scanner failed to run")]
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

    private static ReviewResult WithCost(CostSummary cost) =>
        new(1, Commit, Verdict.NoFindings, [], [], new Dictionary<string, int>()) { Cost = cost };
}
