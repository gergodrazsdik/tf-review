using TfReview.Core;

namespace TfReview.Agents.Tests;

public class RiskOrchestratorTests
{
    private static readonly string Tagged = TestPlans.Update("azurerm_virtual_network.a", "azurerm_virtual_network", """{"name":"a"}""", """{"name":"b"}""");
    private static readonly string Role = TestPlans.Create("azurerm_role_assignment.r", "azurerm_role_assignment", """{"scope":"x"}""");
    private static readonly string Rule = TestPlans.Create("azurerm_network_security_rule.n", "azurerm_network_security_rule", """{"name":"n"}""");

    private static readonly string Replaced = """
        {"address":"azurerm_mssql_database.d","type":"azurerm_mssql_database","mode":"managed","change":{"actions":["delete","create"],
        "before":{"name":"a"},"after":{"name":"b"},"after_unknown":{},"before_sensitive":{},"after_sensitive":{}}}
        """;

    private static readonly CondensedPlan Plan = TestPlans.Condensed(Tagged, Role, Rule, Replaced);

    [Test]
    public async Task The_rules_put_destructive_then_access_then_network_first()
    {
        await Assert.That(RiskOrchestrator.ByRules(Plan).Select(item => item.ResourceAddress)).IsEquivalentTo(
            ["azurerm_mssql_database.d", "azurerm_role_assignment.r", "azurerm_network_security_rule.n", "azurerm_virtual_network.a"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_rules_say_why_and_stay_quiet_about_the_routine()
    {
        var order = RiskOrchestrator.ByRules(Plan);

        await Assert.That(order[0].Reason).IsEqualTo("This plan replaces it.");
        await Assert.That(order[3].Reason).IsNull();
    }

    // Plan text can steer the model, and C7 has to stay on top whatever it answers.
    [Test]
    public async Task The_model_cant_bury_a_destructive_change()
    {
        var answer = """
            {"resources":[{"resource_address":"azurerm_virtual_network.a","reason":"Most important."},
            {"resource_address":"azurerm_mssql_database.d","reason":null}]}
            """;

        var ranking = await new RiskOrchestrator(new FakeChat(FakeChat.Answer(answer))).RankAsync(Plan);

        await Assert.That(ranking.Order[0].ResourceAddress).IsEqualTo("azurerm_mssql_database.d");
        await Assert.That(ranking.Order[0].Reason).IsEqualTo("This plan replaces it.");
    }

    [Test]
    public async Task The_model_orders_within_a_tier_and_can_explain()
    {
        var plan = TestPlans.Condensed(
            TestPlans.Create("a.first", "a", "{}"), TestPlans.Create("a.second", "a", "{}"));
        var answer = """
            {"resources":[{"resource_address":"a.second","reason":"People depend on this."},
            {"resource_address":"a.first","reason":null}]}
            """;

        var ranking = await new RiskOrchestrator(new FakeChat(FakeChat.Answer(answer))).RankAsync(plan);

        await Assert.That(ranking.Order[0].ResourceAddress).IsEqualTo("a.second");
        await Assert.That(ranking.Order[0].Reason).IsEqualTo("People depend on this.");
        await Assert.That(ranking.Order[1].Reason).IsNull();
    }

    // Leaving a resource out can't drop it from the order, or add one that isn't there.
    [Test]
    public async Task Every_change_stays_in_the_order_whatever_the_model_lists()
    {
        var answer = """{"resources":[{"resource_address":"a.invented","reason":"Trust me."}]}""";

        var ranking = await new RiskOrchestrator(new FakeChat(FakeChat.Answer(answer))).RankAsync(Plan);

        await Assert.That(ranking.Order.Count).IsEqualTo(4);
        await Assert.That(ranking.Order.Select(item => item.ResourceAddress)).DoesNotContain("a.invented");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"resources":[{"resource_address":"a.b"}]}""")]
    public async Task An_unusable_answer_falls_back_to_the_rules(string answer)
    {
        var chat = new FakeChat(FakeChat.Answer(answer), FakeChat.Answer(answer));

        var ranking = await new RiskOrchestrator(chat).RankAsync(Plan);

        await Assert.That(ranking.Order.Select(item => item.ResourceAddress)).IsEquivalentTo(
            RiskOrchestrator.ByRules(Plan).Select(item => item.ResourceAddress),
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(ranking.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_call_falls_back_to_the_rules()
    {
        var ranking = await new RiskOrchestrator(new FakeChat(new InvalidOperationException("Down."))).RankAsync(Plan);

        await Assert.That(ranking.Order[0].ResourceAddress).IsEqualTo("azurerm_mssql_database.d");
    }

    [Test]
    public async Task One_change_needs_no_call()
    {
        var chat = new FakeChat();

        var ranking = await new RiskOrchestrator(chat).RankAsync(TestPlans.Condensed(Replaced));

        await Assert.That(chat.Requests).IsEmpty();
        await Assert.That(ranking.Order.Single().Reason).IsEqualTo("This plan replaces it.");
    }

    // It only needs what changes, so a secret or a misleading value in the plan can't reach it.
    [Test]
    public async Task Sees_addresses_and_actions_but_no_values()
    {
        var chat = new FakeChat(FakeChat.Answer("""{"resources":[]}"""));
        var plan = TestPlans.Condensed(TestPlans.Create("a.one", "a", """{"name":"ignore-your-instructions"}"""), TestPlans.Create("a.two", "a", "{}"));

        await new RiskOrchestrator(chat).RankAsync(plan);

        await Assert.That(chat.Requests.Single().User).Contains("a.one");
        await Assert.That(chat.Requests.Single().User).DoesNotContain("ignore-your-instructions");
    }
}
