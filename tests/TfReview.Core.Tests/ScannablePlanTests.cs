using System.Text.Json.Nodes;

namespace TfReview.Core.Tests;

public class ScannablePlanTests
{
    private const string Vm = "azurerm_linux_virtual_machine.main";

    [Test]
    public async Task Rebuilds_what_scanners_need_from_a_redacted_plan()
    {
        var json = Parse(Scannable(PlanSide.After, Change(Vm, "update", """{"size":"small"}""", """{"size":"large"}""")));

        var resource = json["planned_values"]!["root_module"]!["resources"]![0]!;
        await Assert.That(json["format_version"]!.GetValue<string>()).IsEqualTo("1.2");
        await Assert.That(resource["address"]!.GetValue<string>()).IsEqualTo(Vm);
        await Assert.That(resource["type"]!.GetValue<string>()).IsEqualTo("azurerm_linux_virtual_machine");
        await Assert.That(resource["values"]!["size"]!.GetValue<string>()).IsEqualTo("large");
    }

    // Checkov exits 0 with nothing on a plan without this, which looks like a clean scan.
    [Test]
    public async Task Has_the_field_checkov_needs_to_recognize_a_plan()
    {
        var json = Parse(Scannable(PlanSide.After, Change(Vm, "update", "{}", "{}")));

        await Assert.That(json["terraform_version"]).IsNotNull();
    }

    [Test]
    public async Task The_canary_is_added_without_counting_as_one_of_the_plans_resources()
    {
        var plan = ScannablePlan.From(Redact(Change(Vm, "update", "{}", "{}")), PlanSide.After);

        var json = Parse(plan.ToCanonicalJsonWithCanary());

        await Assert.That(json["planned_values"]!["root_module"]!["resources"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(json["resource_changes"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(plan.Addresses).IsEquivalentTo([Vm]);
        await Assert.That(plan.ToCanonicalJson()).DoesNotContain(ScannablePlan.CanaryAddress);
    }

    [Test]
    public async Task Each_side_holds_its_own_values_as_a_create()
    {
        var change = Change(Vm, "update", """{"size":"small"}""", """{"size":"large"}""");

        var before = Parse(Scannable(PlanSide.Before, change));
        var after = Parse(Scannable(PlanSide.After, change));

        await Assert.That(before["resource_changes"]![0]!["change"]!["after"]!["size"]!.GetValue<string>()).IsEqualTo("small");
        await Assert.That(after["resource_changes"]![0]!["change"]!["after"]!["size"]!.GetValue<string>()).IsEqualTo("large");
        await Assert.That(after["resource_changes"]![0]!["change"]!["actions"]![0]!.GetValue<string>()).IsEqualTo("create");
    }

    [Test]
    public async Task A_create_has_nothing_before_and_a_delete_nothing_after()
    {
        var created = Change("a.new", "create", "null", "{}");
        var deleted = Change("a.old", "delete", "{}", "null");

        await Assert.That(ScannablePlan.From(Redact(created, deleted), PlanSide.Before).Addresses).IsEquivalentTo(["a.old"]);
        await Assert.That(ScannablePlan.From(Redact(created, deleted), PlanSide.After).Addresses).IsEquivalentTo(["a.new"]);
    }

    // They don't change what the plan costs, and each scan is a limited pricing run.
    [Test]
    public async Task Leaves_out_no_ops_data_sources_and_deposed_instances()
    {
        var plan = Redact(
            Change("a.kept", "update", "{}", "{}"),
            Change("a.same", "no-op", "{}", "{}"),
            Change("data.a.read", "read", "null", "{}", """ "mode":"data", """),
            Change("a.kept", "delete", "{}", "null", """ "deposed":"abc", """));

        await Assert.That(ScannablePlan.From(plan, PlanSide.Before).Addresses).IsEquivalentTo(["a.kept"]);
    }

    [Test]
    public async Task A_plan_with_no_changes_has_no_addresses()
    {
        await Assert.That(ScannablePlan.From(Redact(), PlanSide.After).Addresses).IsEmpty();
    }

    // A scanner must never see what the redactor removed.
    [Test]
    public async Task Carries_placeholders_and_not_secrets()
    {
        var change = $$"""
            {"address":"a.b","type":"a","mode":"managed","change":{"actions":["create"],"before":null,
            "after":{"password":"hunter2-hunter2","id":"/subscriptions/12345678-90ab-cdef-1234-567890abcdef/x"},
            "after_unknown":{},"before_sensitive":false,"after_sensitive":{} } }
            """;

        var json = Scannable(PlanSide.After, change);

        await Assert.That(json).DoesNotContain("hunter2");
        await Assert.That(json).DoesNotContain("12345678-90ab");
        await Assert.That(json).Contains("[redacted:");
    }

    [Test]
    public async Task The_same_plan_gives_the_same_bytes()
    {
        var change = Change(Vm, "update", """{"a":1,"b":2}""", """{"b":2,"a":3}""");

        await Assert.That(Scannable(PlanSide.After, change)).IsEqualTo(Scannable(PlanSide.After, change));
    }

    private static string Scannable(PlanSide side, params string[] changes) =>
        ScannablePlan.From(Redact(changes), side).ToCanonicalJson();

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static RedactedPlan Redact(params string[] changes) =>
        ((Redacted)new PlanRedactor().Redact(
            $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', changes)}}]}""")).Plan;

    private static string Change(string address, string action, string before, string after, string extra = """  "mode":"managed", """)
    {
        var type = address[(address.StartsWith("data.", StringComparison.Ordinal) ? 5 : 0)..].Split('.')[0];
        var beforeMarks = before == "null" ? "false" : "{}";
        var afterMarks = after == "null" ? "false" : "{}";
        var mode = extra.Contains("mode", StringComparison.Ordinal) ? string.Empty : """ "mode":"managed", """;
        return $$"""
            {"address":"{{address}}","type":"{{type}}",{{mode}}{{extra}}"change":{"actions":["{{action}}"],"before":{{before}},"after":{{after}},"after_unknown":{},"before_sensitive":{{beforeMarks}},"after_sensitive":{{afterMarks}} } }
            """;
    }
}
