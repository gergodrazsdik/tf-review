using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

public class EvidenceCheckerTests
{
    private const string Vnet = "azurerm_virtual_network.main";
    private const string Subnet = "azurerm_subnet.main";
    private const string Updated = "azurerm_virtual_network.updated";

    private static readonly StandardsDoc Naming = TestPlans.Doc("ADR-001", "azurerm_virtual_network");
    private static readonly StandardsDoc Tagging = TestPlans.Doc("ADR-002", "*");

    private static readonly CondensedPlan Plan = TestPlans.Condensed(
        TestPlans.Create(Vnet, "azurerm_virtual_network", """{"name":"BadName","tags":null}"""),
        TestPlans.Create(Subnet, "azurerm_subnet", """{"name":"snet"}"""));

    private static readonly CondensedPlan UpdatePlan = TestPlans.Condensed(TestPlans.Update(
        Updated,
        "azurerm_virtual_network",
        """{"name":"BadName","tags":{"environment":"prod"}}""",
        """{"name":"BadName","tags":{"environment":"production"}}"""));

    [Test]
    public async Task Keeps_a_model_finding_with_valid_evidence()
    {
        var finding = ModelFinding(Vnet, "ADR-001", CondensedPlan.PointerTo(Vnet, "change", "after", "name"));

        var check = Check(finding);

        await Assert.That(check.Kept.Single()).IsEqualTo(finding);
        await Assert.That(check.Drops).IsEmpty();
    }

    // C5: a tag-less resource has "tags": null, and that null is the evidence.
    [Test]
    public async Task A_pointer_to_a_null_attribute_counts()
    {
        var check = Check(ModelFinding(Vnet, "ADR-002", CondensedPlan.PointerTo(Vnet, "change", "after", "tags")));

        await Assert.That(check.Kept.Count).IsEqualTo(1);
    }

    // C8: the condensed plan keeps an update's name for context, which invites comment on an old problem.
    [Test]
    public async Task Drops_a_model_finding_on_an_attribute_the_plan_doesnt_change()
    {
        var check = EvidenceChecker.Check(
            [ModelFinding(Updated, "ADR-001", CondensedPlan.PointerTo(Updated, "change", "after", "name"))], UpdatePlan, [Naming]);

        await Assert.That(check.Kept).IsEmpty();
        await Assert.That(check.Drops[EvidenceChecker.AttributeUnchanged]).IsEqualTo(1);
    }

    // C9: the tag that breaks the standard is one that changes.
    [Test]
    public async Task Keeps_a_model_finding_on_an_attribute_the_plan_changes()
    {
        var check = EvidenceChecker.Check(
            [ModelFinding(Updated, "ADR-002", CondensedPlan.PointerTo(Updated, "change", "after", "tags"))], UpdatePlan, [Tagging]);

        await Assert.That(check.Kept.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_policy_can_still_report_an_old_problem()
    {
        Finding finding = new(
            Updated, "ADR-001", Severity.Low, Reporter.Scanner, true, "t", [new PlanEvidence(CondensedPlan.PointerTo(Updated, "change", "after", "name"))]);

        var check = EvidenceChecker.Check([finding], UpdatePlan, [Naming]);

        await Assert.That(check.Kept.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Drops_a_finding_with_no_pointer()
    {
        var finding = ModelFinding(Vnet, "ADR-001", null);

        await AssertDropped(finding, EvidenceChecker.NoPlanPointer);
    }

    // C10: a subnet has no tags attribute, so a model complaining about its tags has nothing to point at.
    [Test]
    public async Task Drops_a_pointer_to_an_attribute_the_resource_doesnt_have()
    {
        var finding = ModelFinding(Subnet, "ADR-002", CondensedPlan.PointerTo(Subnet, "change", "after", "tags"));

        await AssertDropped(finding, EvidenceChecker.PointerUnresolved);
    }

    [Test]
    public async Task Drops_a_resource_the_plan_doesnt_have()
    {
        const string Invented = "azurerm_virtual_network.invented";

        await AssertDropped(ModelFinding(Invented, "ADR-001", CondensedPlan.PointerTo(Invented, "change", "after")), EvidenceChecker.PointerUnresolved);
    }

    [Test]
    public async Task Drops_a_pointer_into_another_resource()
    {
        var finding = ModelFinding(Subnet, "ADR-002", CondensedPlan.PointerTo(Vnet, "change", "after", "name"));

        await AssertDropped(finding, EvidenceChecker.PointerOtherResource);
    }

    [Test]
    public async Task Drops_a_model_finding_with_no_doc()
    {
        var finding = new Finding(
            Vnet, "ADR-001", Severity.Low, Reporter.Conventions, false, "t", [new PlanEvidence(CondensedPlan.PointerTo(Vnet, "change", "after"))]);

        await AssertDropped(finding, EvidenceChecker.NoDocument);
    }

    // Accepted or not, a doc the model never saw can only have been made up or remembered.
    [Test]
    public async Task Drops_a_doc_that_wasnt_in_the_prompt()
    {
        var finding = ModelFinding(Vnet, "POL-001", CondensedPlan.PointerTo(Vnet, "change", "after", "name"));

        await AssertDropped(finding, EvidenceChecker.DocumentNotRetrieved);
    }

    [Test]
    public async Task Drops_a_doc_that_doesnt_apply_to_the_resource_type()
    {
        var finding = ModelFinding(Subnet, "ADR-001", CondensedPlan.PointerTo(Subnet, "change", "after", "name"));

        await AssertDropped(finding, EvidenceChecker.DocumentNotApplicable);
    }

    // A policy can cite a doc outside the agent's topics, like POL-001, and a scanner rule may have no doc.
    [Test]
    public async Task A_scanner_finding_only_needs_its_pointer()
    {
        Finding finding = new(
            Vnet, "POL-001", Severity.High, Reporter.Scanner, true, "t", [new PlanEvidence(CondensedPlan.PointerTo(Vnet, "change", "after"))]);

        await Assert.That(Check(finding).Kept.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_scanner_finding_with_a_bad_pointer_is_dropped_too()
    {
        Finding finding = new(
            Vnet, "POL-001", Severity.High, Reporter.Scanner, true, "t", [new PlanEvidence(CondensedPlan.PointerTo(Vnet, "change", "after", "nope"))]);

        await AssertDropped(finding, EvidenceChecker.PointerUnresolved);
    }

    [Test]
    public async Task Counts_drops_by_reason()
    {
        var check = Check(
            ModelFinding(Vnet, "POL-001", CondensedPlan.PointerTo(Vnet, "change", "after")),
            ModelFinding(Vnet, "POL-002", CondensedPlan.PointerTo(Vnet, "change", "after")),
            ModelFinding(Vnet, "ADR-001", null));

        await Assert.That(check.Kept).IsEmpty();
        await Assert.That(check.Drops[EvidenceChecker.DocumentNotRetrieved]).IsEqualTo(2);
        await Assert.That(check.Drops[EvidenceChecker.NoPlanPointer]).IsEqualTo(1);
    }

    private static EvidenceCheck Check(params Finding[] findings) => EvidenceChecker.Check(findings, Plan, [Naming, Tagging]);

    private static async Task AssertDropped(Finding finding, string reason)
    {
        var check = Check(finding);

        await Assert.That(check.Kept).IsEmpty();
        await Assert.That(check.Drops[reason]).IsEqualTo(1);
    }

    private static Finding ModelFinding(string address, string doc, string? pointer)
    {
        Evidence[] evidence = pointer is null
            ? [new DocEvidence(doc)]
            : [new PlanEvidence(pointer), new DocEvidence(doc)];
        return new Finding(address, doc, Severity.Low, Reporter.Conventions, false, "t", evidence);
    }
}
