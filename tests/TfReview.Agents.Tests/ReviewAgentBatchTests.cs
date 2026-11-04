using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

/// <summary>A slice too large for one call, reviewed in batches, riskiest first (map-reduce, M4).</summary>
public class ReviewAgentBatchTests
{
    private const string Empty = """{"policy_suggestions":[],"findings":[]}""";

    private const string Role = "azurerm_role_assignment.a";
    private const string Rule = "azurerm_network_security_rule.b";
    private const string Storage = "azurerm_storage_account.c";
    private const string Vnet = "azurerm_virtual_network.d";

    private static readonly StandardsDoc Tagging = TestPlans.Doc("ADR-002", "*", Severity.Medium, "Tags body.");

    // About 8K estimated tokens each, so two fit in a call and a third doesn't.
    private static string Big(string address) =>
        TestPlans.Create(address, address[..address.IndexOf('.', StringComparison.Ordinal)], $$"""{"name":"{{new string('x', 25_000)}}"}""");

    private static string Finds(string address) =>
        $$"""{"policy_suggestions":[],"findings":[{"breaks_standard":true,"resource_address":"{{address}}","doc_id":"ADR-002","attribute":"name","title":"t","suggestion":null}]}""";

    [Test]
    public async Task Access_and_network_changes_go_in_the_first_batch()
    {
        var plan = TestPlans.Condensed(Big(Vnet), Big(Storage), Big(Rule), Big(Role));
        var chat = new FakeChat(FakeChat.Answer(Empty), FakeChat.Answer(Empty));

        var outcome = await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(chat.Requests.Count).IsEqualTo(2);
        await Assert.That(chat.Requests[0].User).Contains(Role);
        await Assert.That(chat.Requests[0].User).Contains(Rule);
        await Assert.That(chat.Requests[0].User).DoesNotContain(Storage);
        await Assert.That(((AgentReviewed)outcome).Skipped).IsEmpty();
    }

    [Test]
    public async Task Findings_from_every_batch_come_back_together()
    {
        var plan = TestPlans.Condensed(Big(Vnet), Big(Storage), Big(Rule), Big(Role));
        var chat = new FakeChat(FakeChat.Answer(Finds(Role)), FakeChat.Answer(Finds(Vnet)));

        var outcome = (AgentReviewed)await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(outcome.Findings.Select(finding => finding.ResourceAddress)).IsEquivalentTo([Role, Vnet]);
        await Assert.That(outcome.Calls.Count).IsEqualTo(2);
    }

    // Each batch answers its own policy findings by ID, so it must only be shown its own.
    [Test]
    public async Task A_batch_only_sees_its_own_policy_findings()
    {
        var plan = TestPlans.Condensed(Big(Vnet), Big(Storage), Big(Rule), Big(Role));
        var policy = new Finding(
            Vnet, "ADR-002", Severity.Medium, Reporter.Scanner, true, "Policy says no tags", [new PlanEvidence($"/resource_changes/{Vnet}/change/after"), new DocEvidence("ADR-002")]);
        var chat = new FakeChat(FakeChat.Answer(Empty), FakeChat.Answer(Empty));

        await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], [policy]);

        await Assert.That(chat.Requests[0].User).DoesNotContain("Policy says no tags");
        await Assert.That(chat.Requests[1].User).Contains("Policy says no tags");
    }

    [Test]
    public async Task What_the_batch_cap_leaves_is_reported_by_resource()
    {
        var addresses = Enumerable.Range(0, 10).Select(index => $"azurerm_virtual_network.n{index}").ToList();
        var plan = TestPlans.Condensed([.. addresses.Select(Big)]);
        var chat = new FakeChat([.. Enumerable.Repeat<object>(FakeChat.Answer(Empty), ReviewAgent.MaxBatches)]);

        var outcome = (AgentReviewed)await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(chat.Requests.Count).IsEqualTo(ReviewAgent.MaxBatches);
        await Assert.That(outcome.Skipped).IsEquivalentTo(["azurerm_virtual_network.n8", "azurerm_virtual_network.n9"]);
        await Assert.That(outcome.SkippedBecause).IsEqualTo(NotReviewedReason.PlanTooLarge);
    }

    // A failed batch costs its own resources and the rest, but not what was already reviewed.
    [Test]
    public async Task A_failed_batch_stops_the_rest_and_keeps_what_came_before()
    {
        var plan = TestPlans.Condensed(Big(Vnet), Big(Storage), Big(Rule), Big(Role));
        var chat = new FakeChat(FakeChat.Answer(Finds(Role)), FakeChat.Answer("not json"), FakeChat.Answer("not json"));

        var outcome = (AgentReviewed)await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(outcome.Findings.Single().ResourceAddress).IsEqualTo(Role);
        await Assert.That(outcome.Skipped).IsEquivalentTo([Storage, Vnet]);
        await Assert.That(outcome.SkippedBecause).IsEqualTo(NotReviewedReason.InvalidOutput);
    }

    [Test]
    public async Task Nothing_reviewed_is_reported_for_the_whole_scope()
    {
        var plan = TestPlans.Condensed(Big(Vnet), Big(Storage), Big(Rule), Big(Role));
        var chat = new FakeChat(FakeChat.Answer("not json"), FakeChat.Answer("not json"));

        var outcome = await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(((AgentFailed)outcome).Reason).IsEqualTo(NotReviewedReason.InvalidOutput);
        await Assert.That(chat.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_resource_too_large_on_its_own_is_left_out_and_the_rest_reviewed()
    {
        var huge = TestPlans.Create("azurerm_storage_account.huge", "azurerm_storage_account", $$"""{"name":"{{new string('x', 80_000)}}"}""");
        var plan = TestPlans.Condensed(huge, Big(Vnet), Big(Storage), Big(Role));
        var chat = new FakeChat(FakeChat.Answer(Empty), FakeChat.Answer(Empty));

        var outcome = (AgentReviewed)await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(outcome.Skipped).IsEquivalentTo(["azurerm_storage_account.huge"]);
        await Assert.That(outcome.SkippedBecause).IsEqualTo(NotReviewedReason.PlanTooLarge);
    }

    [Test]
    public async Task Batches_fill_in_risk_order_up_to_the_cap()
    {
        var plan = TestPlans.Condensed(
            TestPlans.Create(Vnet, "azurerm_virtual_network", "{}"),
            TestPlans.Create(Storage, "azurerm_storage_account", "{}"),
            TestPlans.Create(Rule, "azurerm_network_security_rule", "{}"),
            TestPlans.Create(Role, "azurerm_role_assignment", "{}"));

        var (batches, left) = ReviewAgent.Batch(plan, addresses => addresses.Count * 10, maxTokens: 20, maxBatches: 1);

        await Assert.That(batches.Single()).IsEquivalentTo([Role, Rule], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(left).IsEquivalentTo([Storage, Vnet], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
