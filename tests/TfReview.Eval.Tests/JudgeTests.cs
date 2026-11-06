using TfReview.Core;

namespace TfReview.Eval.Tests;

public class JudgeTests
{
    private const string Address = "azurerm_network_security_rule.ssh";

    private static readonly ExpectedCase Expected = new([new ExpectedFinding(Address, "POL-002", "security", true)]);

    [Test]
    public async Task A_matching_result_passes()
    {
        await Assert.That(Judge.Problems(Expected, Result(Policy()))).IsEmpty();
    }

    [Test]
    public async Task A_missing_finding_fails()
    {
        await Assert.That(Judge.Problems(Expected, Result())).Contains(problem => problem.StartsWith("missing:", StringComparison.Ordinal));
    }

    [Test]
    public async Task An_extra_finding_fails_like_a_missing_one()
    {
        var extra = Policy() with { Rule = "CKV_AZURE_10", Agent = Reporter.Scanner };

        await Assert.That(Judge.Problems(Expected, Result(Policy(), extra))).Contains(problem => problem.StartsWith("unexpected:", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_same_finding_twice_fails()
    {
        await Assert.That(Judge.Problems(Expected, Result(Policy(), Policy()))).Contains(problem => problem.StartsWith("reported 2 times", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_rule_the_arbiter_folded_into_another_row_counts_as_reported()
    {
        var expected = new ExpectedCase([.. Expected.Findings, new ExpectedFinding(Address, "CKV_AZURE_10", "scanner", true)]);
        var merged = Policy() with { RelatedRules = ["CKV_AZURE_10"] };

        await Assert.That(Judge.Problems(expected, Result(merged))).IsEmpty();
    }

    [Test]
    public async Task A_part_not_reviewed_fails()
    {
        var result = Result(Policy()) with { NotReviewed = [new NotReviewed(ReviewScope.Security, NotReviewedReason.Timeout)] };

        await Assert.That(Judge.Problems(Expected, result)).Contains(problem => problem.StartsWith("not reviewed:", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_wrong_resource_first_in_the_risk_order_fails()
    {
        var expected = Expected with { RiskFirst = Address };
        var result = Result(Policy()) with { RiskOrder = [new RiskItem("azurerm_subnet.a"), new RiskItem(Address)] };

        await Assert.That(Judge.Problems(expected, result)).Contains(problem => problem.StartsWith("risk order:", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_finding_citing_a_standard_needs_the_citation()
    {
        var uncited = Policy() with { Evidence = [new PlanEvidence("/resource_changes/0/change/after/source_address_prefix")] };

        await Assert.That(Judge.Problems(Expected, Result(uncited))).Contains($"{Address} POL-002: no citation");
    }

    [Test]
    public async Task A_finding_needs_a_plan_pointer()
    {
        var unpointed = Policy() with { Evidence = [new DocEvidence("POL-002")] };

        await Assert.That(Judge.Problems(Expected, Result(unpointed))).Contains($"{Address} POL-002: no plan pointer");
    }

    [Test]
    public async Task A_cost_finding_needs_its_figure()
    {
        var vm = "azurerm_linux_virtual_machine.main";
        var expected = new ExpectedCase([new ExpectedFinding(vm, "ADR-003", "cost", false)]);
        var cost = new Finding(vm, "ADR-003", Severity.Medium, Reporter.Cost, false, "Adds $100 a month",
            [new DocEvidence("ADR-003"), new PlanEvidence("/resource_changes/0/change/after/size")]);

        await Assert.That(Judge.Problems(expected, Result(cost))).Contains($"{vm} ADR-003: no cost figure");
        await Assert.That(Judge.Problems(expected, Result(cost with { MonthlyCostDeltaUsd = 100m }))).IsEmpty();
    }

    [Test]
    public async Task A_missing_finding_scores_none_of_its_concepts()
    {
        var score = new SuggestionScore([new SuggestionExpectation(Address, "POL-002", new Dictionary<string, string> { ["a"] = "x", ["b"] = "y" })]);

        var (found, asked, missed) = score.Score(Array.Empty<ScoredFinding>());

        await Assert.That(found).IsEqualTo(0);
        await Assert.That(asked).IsEqualTo(2);
        await Assert.That(missed).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Held_out_and_tuned_cases_are_counted_apart()
    {
        var tally = new Tally();
        tally.Add(heldOut: true, passed: 4, runs: 5, found: 2, asked: 3);
        tally.Add(heldOut: false, passed: 5, runs: 5, found: 3, asked: 3);
        tally.Add(heldOut: false, passed: 5, runs: 5, found: 0, asked: 0);

        await Assert.That(tally.Lines().ToList()).IsEquivalentTo(new[]
        {
            "Held out: 1 case(s), runs passing 4 of 5, suggestion concepts 2 of 3.",
            "Tuned on or seen: 2 case(s), runs passing 10 of 10, suggestion concepts 3 of 3.",
        });
    }

    private static Finding Policy() => new(
        Address, "POL-002", Severity.High, Reporter.Security, true, "SSH from the internet",
        [new DocEvidence("POL-002"), new PlanEvidence("/resource_changes/0/change/after/source_address_prefix")]);

    private static ReviewResult Result(params Finding[] findings) =>
        new(1, new string('a', 40), findings.Length == 0 ? Verdict.NoFindings : Verdict.Findings, findings, [], new Dictionary<string, int>());
}
