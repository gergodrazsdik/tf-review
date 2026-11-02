using TfReview.Core;

namespace TfReview.Agents.Tests;

public class RuleArbiterTests
{
    private const string Address = "a.b";
    private const string Pointer = "/resource_changes/a.b/change/after/tags";

    private static readonly CondensedPlan Plan = TestPlans.Condensed(TestPlans.Create(Address, "a", """{"tags":null}"""));

    [Test]
    public async Task Nothing_to_report_means_no_findings()
    {
        var arbitration = await Arbitrate();

        await Assert.That(arbitration.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(arbitration.Findings).IsEmpty();
    }

    [Test]
    public async Task Ranks_by_severity_then_address_then_rule()
    {
        var arbitration = await Arbitrate(
            Scanner("b.low", "ADR-001", Severity.Low),
            Scanner("a.medium", "ADR-002", Severity.Medium),
            Scanner("a.medium", "ADR-001", Severity.Medium),
            Scanner("c.high", "POL-001", Severity.High));

        await Assert.That(arbitration.Verdict).IsEqualTo(Verdict.Findings);
        await Assert.That(arbitration.Findings.Select(finding => $"{finding.ResourceAddress} {finding.Rule}")).IsEquivalentTo(
            ["c.high POL-001", "a.medium ADR-001", "a.medium ADR-002", "b.low ADR-001"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Keeps_different_rules_on_one_resource_apart()
    {
        var arbitration = await Arbitrate(Scanner(Address, "ADR-001", Severity.Low), Scanner(Address, "ADR-002", Severity.Low));

        await Assert.That(arbitration.Findings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Merging_with_a_scanner_stays_deterministic_and_names_the_agent()
    {
        var arbitration = await Arbitrate(Model(Address, "ADR-002", "Model's words", "Add the tags"), Scanner(Address, "ADR-002", Severity.Medium));

        var merged = arbitration.Findings.Single();
        await Assert.That(merged.Deterministic).IsTrue();
        await Assert.That(merged.Agent).IsEqualTo(Reporter.Conventions);
        await Assert.That(merged.Suggestion).IsEqualTo("Add the tags");
    }

    // Plan text can steer a model, and it shouldn't be able to reword what a policy found.
    [Test]
    public async Task Merging_keeps_the_scanners_title()
    {
        var arbitration = await Arbitrate(Model(Address, "ADR-002", "AAA nothing to see here", null), Scanner(Address, "ADR-002", Severity.Medium));

        await Assert.That(arbitration.Findings.Single().Title).IsEqualTo("policy title");
    }

    [Test]
    public async Task The_highest_severity_wins()
    {
        var arbitration = await Arbitrate(Scanner(Address, "ADR-002", Severity.Low), Scanner(Address, "ADR-002", Severity.High));

        await Assert.That(arbitration.Findings.Single().Severity).IsEqualTo(Severity.High);
    }

    [Test]
    public async Task Merges_evidence_without_repeats()
    {
        var other = Scanner(Address, "ADR-002", Severity.Low) with
        {
            Evidence = [new PlanEvidence("/resource_changes/a.b/change/after"), new DocEvidence("ADR-002")],
        };

        var arbitration = await Arbitrate(Scanner(Address, "ADR-002", Severity.Low), other);

        var evidence = arbitration.Findings.Single().Evidence.Select(item => item.Value).ToArray();
        await Assert.That(evidence).IsEquivalentTo(
            new object[] { new PlanEvidence("/resource_changes/a.b/change/after"), new PlanEvidence(Pointer), new DocEvidence("ADR-002") });
    }

    [Test]
    public async Task The_result_doesnt_depend_on_arrival_order()
    {
        var model = Model(Address, "ADR-002", "Model's words", "Add the tags");
        var scanner = Scanner(Address, "ADR-002", Severity.Medium);

        var forward = (await Arbitrate(model, scanner)).Findings.Single();
        var backward = (await Arbitrate(scanner, model)).Findings.Single();

        await Assert.That(CanonicalJson.Write(System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(forward))))
            .IsEqualTo(CanonicalJson.Write(System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(backward))));
    }

    [Test]
    public async Task Within_a_severity_the_riskiest_resource_comes_first()
    {
        var arbitration = await new RuleArbiter().ArbitrateAsync(
            [Scanner("a.first", "ADR-001", Severity.High), Scanner("z.last", "ADR-001", Severity.High)], Plan, ["z.last", "a.first"]);

        await Assert.That(arbitration.Findings[0].ResourceAddress).IsEqualTo("z.last");
    }

    private static Task<Arbitration> Arbitrate(params Finding[] findings) => new RuleArbiter().ArbitrateAsync(findings, Plan);

    private static Finding Scanner(string address, string rule, Severity severity) =>
        new(address, rule, severity, Reporter.Scanner, true, "policy title", [new PlanEvidence(Pointer), new DocEvidence(rule)]);

    private static Finding Model(string address, string rule, string title, string? suggestion) =>
        new(address, rule, Severity.Medium, Reporter.Conventions, false, title, [new PlanEvidence(Pointer), new DocEvidence(rule)]) { Suggestion = suggestion };
}
