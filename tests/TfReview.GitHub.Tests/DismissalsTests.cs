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

        var body = CommentRenderer.Render(Result(finding), null, new HashSet<string> { Dismissals.Key(finding) });

        var dismissal = Dismissals.Ticked(body).Single();
        await Assert.That(dismissal.Recorded).IsTrue();
    }

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
