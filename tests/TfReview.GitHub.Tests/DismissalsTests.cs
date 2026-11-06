using TfReview.Core;

namespace TfReview.GitHub.Tests;

/// <summary>The comment's dismiss checkboxes (D56).</summary>
public class DismissalsTests
{
    private static readonly string Commit = new('a', 40);

    private static Finding Finding(string address, string rule = "POL-002") =>
        new(address, rule, Severity.High, Reporter.Security, true, "SSH", [new DocEvidence(rule)]);

    private static ReviewResult Result(params Finding[] findings) =>
        new(1, Commit, Verdict.Findings, findings, [], new Dictionary<string, int>());

    [Test]
    public async Task A_finding_keeps_its_key_across_pushes_and_another_doesnt_share_it()
    {
        var key = Dismissals.Key(Finding("azurerm_network_security_rule.a"));

        await Assert.That(Dismissals.Key(Finding("azurerm_network_security_rule.a") with { Title = "Other words" })).IsEqualTo(key);
        await Assert.That(Dismissals.Key(Finding("azurerm_network_security_rule.b"))).IsNotEqualTo(key);
        await Assert.That(Dismissals.Key(Finding("azurerm_network_security_rule.a", "POL-001"))).IsNotEqualTo(key);
    }

    [Test]
    public async Task Reads_what_a_reviewer_ticked_and_ignores_the_rest()
    {
        var body = CommentRenderer.Render(Result(Finding("azurerm_network_security_rule.a"), Finding("azurerm_subnet.b", "ADR-001")), null);
        var ticked = body.Replace("- [ ] Dismiss `azurerm_subnet.b`", "- [x] Dismiss `azurerm_subnet.b`", StringComparison.Ordinal);

        var dismissal = Dismissals.Ticked(ticked).Single();

        await Assert.That(dismissal.Key).IsEqualTo(Dismissals.Key(Finding("azurerm_subnet.b", "ADR-001")));
        await Assert.That(dismissal.Rule).IsEqualTo("ADR-001");
        await Assert.That(dismissal.Recorded).IsFalse();
    }

    // GitHub can store a body edited in the browser with Windows line endings.
    [Test]
    public async Task Reads_a_tick_with_windows_line_endings()
    {
        var body = CommentRenderer.Render(Result(Finding("azurerm_subnet.b")), null).Replace("- [ ]", "- [X]", StringComparison.Ordinal).ReplaceLineEndings("\r\n");

        await Assert.That(Dismissals.Ticked(body)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_finding_dismissed_before_stays_ticked_and_isnt_counted_again()
    {
        var finding = Finding("azurerm_subnet.b");

        var body = CommentRenderer.Rewrite(Result(finding), null, Tick(CommentRenderer.Render(Result(finding), null)), record: true).Body;

        var dismissal = Dismissals.Ticked(body).Single();
        await Assert.That(dismissal.Recorded).IsTrue();
        await Assert.That(CommentRenderer.Rewrite(Result(finding), null, body, record: true).CountedDismissals).IsEmpty();
    }

    // BUG-2: a failed review, no findings, or a push where the finding is missing keeps the tick, hidden, until it's back.
    [Test]
    [Arguments(Verdict.NotReviewed)]
    [Arguments(Verdict.NoFindings)]
    [Arguments(Verdict.Findings)]
    public async Task A_tick_survives_a_rewrite_that_doesnt_show_its_finding(Verdict verdict)
    {
        var finding = Finding("azurerm_subnet.b");
        var ticked = Tick(CommentRenderer.Render(Result(finding), null));
        var between = verdict switch
        {
            Verdict.NotReviewed => ReviewResult.Failed(Commit, ReviewScope.Plan, NotReviewedReason.Unavailable),
            Verdict.NoFindings => new ReviewResult(1, Commit, Verdict.NoFindings, [], [], new Dictionary<string, int>()),
            _ => Result(Finding("azurerm_subnet.c")),
        };

        var hidden = CommentRenderer.Rewrite(between, null, ticked, record: true);
        var back = CommentRenderer.Rewrite(Result(finding), null, hidden.Body, record: true);

        await Assert.That(hidden.CountedDismissals).IsEquivalentTo(new string?[] { "POL-002" });
        await Assert.That(back.CountedDismissals).IsEmpty();
        var dismissal = Dismissals.Ticked(back.Body).Single();
        await Assert.That(dismissal.Key).IsEqualTo(Dismissals.Key(finding));
        await Assert.That(dismissal.Recorded).IsTrue();
        await Assert.That(back.Body).Contains("- [x] Dismiss `azurerm_subnet.b`");
    }

    // The workflow's fallback carries ticks over but leaves them to the worker to count, which has the metrics.
    [Test]
    public async Task A_rewrite_that_doesnt_record_carries_ticks_uncounted()
    {
        var finding = Finding("azurerm_subnet.b");
        var ticked = Tick(CommentRenderer.Render(Result(finding), null));

        var fallback = CommentRenderer.Rewrite(ReviewResult.Failed(Commit, ReviewScope.Plan, NotReviewedReason.Unavailable), null, ticked, record: false);
        var next = CommentRenderer.Rewrite(Result(finding), null, fallback.Body, record: true);

        await Assert.That(fallback.CountedDismissals).IsEmpty();
        await Assert.That(next.CountedDismissals).IsEquivalentTo(new string?[] { "POL-002" });
    }

    // BUG-7: a box unticked after it was counted, then ticked again, isn't counted a second time.
    [Test]
    public async Task A_tick_counted_once_isnt_counted_again_after_an_untick()
    {
        var finding = Finding("azurerm_subnet.b");
        var counted = CommentRenderer.Rewrite(Result(finding), null, Tick(CommentRenderer.Render(Result(finding), null)), record: true).Body;
        var unticked = counted.Replace("- [x]", "- [ ]", StringComparison.Ordinal);

        var kept = CommentRenderer.Rewrite(Result(finding), null, unticked, record: true).Body;
        var again = CommentRenderer.Rewrite(Result(finding), null, Tick(kept), record: true);

        await Assert.That(kept).Contains("recorded -->");
        await Assert.That(again.CountedDismissals).IsEmpty();
    }

    // BUG-23: a tick stays when the arbiter merges its row into another one on the next push, or splits it out again.
    [Test]
    public async Task A_tick_follows_its_rule_through_a_merge_and_back()
    {
        var alone = Finding("azurerm_subnet.b", "ADR-002");
        var merged = Finding("azurerm_subnet.b", "POL-001") with { RelatedRules = ["ADR-002"] };
        var ticked = Tick(CommentRenderer.Render(Result(alone), null));

        var asMerged = CommentRenderer.Rewrite(Result(merged), null, ticked, record: true).Body;
        var apart = CommentRenderer.Rewrite(Result(alone, Finding("azurerm_subnet.b", "POL-001")), null, asMerged, record: true);

        await Assert.That(asMerged).Contains("- [x] Dismiss `azurerm_subnet.b` (`POL-001`)");
        await Assert.That(apart.Body).Contains("- [x] Dismiss `azurerm_subnet.b` (`ADR-002`)");
        await Assert.That(apart.CountedDismissals).IsEmpty();
    }

    // The feedback script reads shown lines only, so a hidden one mustn't look like one.
    [Test]
    public async Task A_hidden_line_isnt_a_shown_one()
    {
        var ticked = Tick(CommentRenderer.Render(Result(Finding("azurerm_subnet.b")), null));

        var hidden = CommentRenderer.Rewrite(ReviewResult.Failed(Commit, ReviewScope.Plan, NotReviewedReason.Unavailable), null, ticked, record: true).Body;

        await Assert.That(hidden).DoesNotContain("- [x]");
        await Assert.That(hidden).Contains("<!-- tf-review:dismissed ");
    }

    private static string Tick(string body) => body.Replace("- [ ]", "- [x]", StringComparison.Ordinal);

    // The address comes from the PR's plan, so it mustn't be able to make a line of its own, ticked or not.
    [Test]
    public async Task An_address_cant_forge_a_ticked_line()
    {
        var forged = "a.b\n- [x] Dismiss x <!-- tf-review:dismiss 0123456789abcdef rule=POL-001 -->";

        var body = CommentRenderer.Render(Result(Finding(forged)), null);

        await Assert.That(Dismissals.Ticked(body)).IsEmpty();
    }

    // A rule ID goes into the marker only when it's a plain identifier; anything else is left out, not escaped.
    [Test]
    public async Task A_rule_that_isnt_an_identifier_stays_out_of_the_marker()
    {
        var marker = Dismissals.Marker(Finding("a.b", "POL-1 --> <b>"), recorded: false);

        await Assert.That(marker).DoesNotContain("POL-1");
        await Assert.That(marker).EndsWith(" -->");
    }
}
