using System.Text.Json.Nodes;

namespace TfReview.Core.Tests;

public class CondensedPlanTests
{
    private const string Address = "azurerm_virtual_network.main";

    [Test]
    public async Task Knows_the_types_that_change()
    {
        var plan = Condense(Plan(
            Resource("a.one", "[\"create\"]", "null", "{}"),
            Resource("a.two", "[\"create\"]", "null", "{}")));

        await Assert.That(plan.TypeOf("a.two")).IsEqualTo("x");
        await Assert.That(plan.TypeOf("c.missing")).IsNull();
        await Assert.That(plan.ResourceTypes()).IsEquivalentTo(["x"]);
    }

    [Test]
    public async Task Drops_no_ops()
    {
        var plan = Condense(Plan(Resource("a.keep", "[\"update\"]", """{"x":1}""", """{"x":2}"""), Resource("a.same", "[\"no-op\"]", """{"x":1}""", """{"x":1}""")));

        await Assert.That(Resources(plan).Select(field => field.Key).ToArray()).IsEquivalentTo(["a.keep"]);
    }

    [Test]
    public async Task Sorts_by_address_whatever_the_input_order()
    {
        var plan = Condense(Plan(Resource("b.second", "[\"create\"]", "null", "{}"), Resource("a.first", "[\"create\"]", "null", "{}")));

        await Assert.That(Resources(plan).Select(field => field.Key).ToArray()).IsEquivalentTo(["a.first", "b.second"]);
        await Assert.That(plan.ToCanonicalJson().IndexOf("a.first", StringComparison.Ordinal))
            .IsLessThan(plan.ToCanonicalJson().IndexOf("b.second", StringComparison.Ordinal));
    }

    [Test]
    public async Task Removes_attributes_that_didnt_change()
    {
        var change = Change(Condense(Plan(Resource(
            Address, "[\"update\"]", """{"location":"germany","sku":"a"}""", """{"location":"germany","sku":"b"}"""))));

        await Assert.That(change["after"]!.AsObject().ContainsKey("location")).IsFalse();
        await Assert.That(change["before"]!["sku"]!.GetValue<string>()).IsEqualTo("a");
        await Assert.That(change["after"]!["sku"]!.GetValue<string>()).IsEqualTo("b");
    }

    // C8: otherwise updating one tag's value looks like a resource with no name, and invites a false finding.
    [Test]
    public async Task Keeps_name_and_tags_even_when_unchanged()
    {
        var change = Change(Condense(Plan(Resource(
            Address, "[\"update\"]",
            """{"name":"vnet-a-prod","tags":{"owner":"x"},"sku":"a"}""",
            """{"name":"vnet-a-prod","tags":{"owner":"x"},"sku":"b"}"""))));

        await Assert.That(change["after"]!["name"]!.GetValue<string>()).IsEqualTo("vnet-a-prod");
        await Assert.That(change["after"]!["tags"]!["owner"]!.GetValue<string>()).IsEqualTo("x");
    }

    [Test]
    public async Task Keeps_a_changed_attribute_whole_so_list_indexes_dont_shift()
    {
        var change = Change(Condense(Plan(Resource(
            Address, "[\"update\"]", """{"subnets":["a","b","c"]}""", """{"subnets":["a","x","c"]}"""))));

        await Assert.That(change["after"]!["subnets"]!.AsArray().Count).IsEqualTo(3);
        await Assert.That(change["after"]!["subnets"]![2]!.GetValue<string>()).IsEqualTo("c");
    }

    // C5: the null is the evidence, and a finding's pointer has to resolve to it.
    [Test]
    public async Task Keeps_nulls_on_a_create()
    {
        var plan = Condense(Plan(Resource(Address, "[\"create\"]", "null", """{"tags":null,"name":"vnet-a-prod"}""")));

        await Assert.That(plan.Resolves(CondensedPlan.PointerTo(Address, "change", "after", "tags"))).IsTrue();
    }

    [Test]
    public async Task Keeps_everything_on_a_delete()
    {
        var change = Change(Condense(Plan(Resource(Address, "[\"delete\"]", """{"sku":"a"}""", "null"))));

        await Assert.That(change["before"]!["sku"]!.GetValue<string>()).IsEqualTo("a");
        await Assert.That(change["after"]).IsNull();
    }

    [Test]
    public async Task A_value_becoming_unknown_counts_as_a_change()
    {
        var change = Change(Condense(Plan(Resource(
            Address, "[\"update\"]", """{"id":"old","sku":"a"}""", """{"sku":"a"}""", """{"id":true}"""))));

        await Assert.That(change["before"]!["id"]!.GetValue<string>()).IsEqualTo("old");
        await Assert.That(change["after_unknown"]!["id"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task An_attribute_removed_by_the_update_counts_as_a_change()
    {
        var change = Change(Condense(Plan(Resource(Address, "[\"update\"]", """{"sku":"a"}""", "{}"))));

        await Assert.That(change["before"]!["sku"]!.GetValue<string>()).IsEqualTo("a");
    }

    [Test]
    public async Task Keeps_why_a_resource_is_replaced()
    {
        var plan = Condense(Plan(Resource(
            Address, "[\"delete\",\"create\"]", """{"name":"a"}""", """{"name":"b"}""", "{}",
            "\"replace_paths\":[[\"name\"]],"), Resource("a.b", "[\"create\"]", "null", "{}")));

        await Assert.That(plan.Resolves(CondensedPlan.PointerTo(Address, "change", "replace_paths", "0", "0"))).IsTrue();
    }

    [Test]
    public async Task Leaves_out_marks_and_outputs()
    {
        var json = Condense(Plan(Resource(Address, "[\"create\"]", "null", "{}"))).ToCanonicalJson();

        await Assert.That(json).DoesNotContain("_sensitive");
        await Assert.That(json).DoesNotContain("output_changes");
    }

    [Test]
    public async Task Carries_no_canary_from_a_real_plan()
    {
        var json = Condense(await File.ReadAllTextAsync("plans/canaries.plan.json")).ToCanonicalJson();

        await Assert.That(json).DoesNotContain("CANARY-sensitive-variable-default");
        await Assert.That(json).DoesNotContain("CANARY-marked-list-item");
        await Assert.That(json).Contains("terraform_data.partly_marked");
    }

    [Test]
    public async Task A_deposed_instance_gets_its_own_key()
    {
        var plan = Condense(Plan(
            Resource(Address, "[\"create\"]", "null", "{}"),
            Resource(Address, "[\"delete\"]", "{}", "null", "{}", "\"deposed\":\"abc123\",", inChange: false)));

        await Assert.That(Resources(plan).Select(field => field.Key).ToArray())
            .IsEquivalentTo([Address, $"{Address} (deposed abc123)"]);
    }

    [Test]
    public async Task Pointer_matches_the_contract_form()
    {
        await Assert.That(CondensedPlan.PointerTo("azurerm_storage_account.logs", "change", "after", "tags"))
            .IsEqualTo("/resource_changes/azurerm_storage_account.logs/change/after/tags");
    }

    [Test]
    [Arguments("""module.a.x.y["b/c"]""", """/resource_changes/module.a.x.y["b~1c"]/change""")]
    [Arguments("""x.y["a~b"]""", """/resource_changes/x.y["a~0b"]/change""")]
    public async Task Escapes_and_recovers_awkward_addresses(string address, string pointer)
    {
        await Assert.That(CondensedPlan.PointerTo(address, "change")).IsEqualTo(pointer);
        await Assert.That(CondensedPlan.AddressOf(pointer)).IsEqualTo(address);
    }

    [Test]
    public async Task Resolves_a_pointer_through_an_awkward_address()
    {
        const string Awkward = """module.a.x.y["b/c"]""";
        var plan = Condense(Plan(Resource(Awkward, "[\"create\"]", "null", """{"tags":null}""")));

        await Assert.That(plan.Resolves(CondensedPlan.PointerTo(Awkward, "change", "after", "tags"))).IsTrue();
    }

    [Test]
    [Arguments("/resource_changes/missing.resource/change")]
    [Arguments("/resource_changes/a.b/change/after/missing")]
    [Arguments("/resource_changes/a.b/change/after/list/9")]
    [Arguments("/resource_changes/a.b/change/after/list/-1")]
    [Arguments("/other/a.b")]
    [Arguments("resource_changes/a.b")]
    [Arguments("")]
    public async Task Doesnt_resolve_a_pointer_to_nothing(string pointer)
    {
        var plan = Condense(Plan(Resource("a.b", "[\"create\"]", "null", """{"list":["x"]}""")));

        await Assert.That(plan.Resolves(pointer)).IsFalse();
    }

    [Test]
    public async Task Doesnt_find_an_address_in_a_pointer_outside_the_changes()
    {
        await Assert.That(CondensedPlan.AddressOf("/other/a.b")).IsNull();
    }

    [Test]
    public async Task The_same_plan_always_condenses_to_the_same_bytes()
    {
        var plan = await File.ReadAllTextAsync("plans/canaries.plan.json");

        await Assert.That(Condense(plan).ToCanonicalJson()).IsEqualTo(Condense(plan).ToCanonicalJson());
    }

    private static CondensedPlan Condense(string json) => new PlanRedactor().Redact(json) switch
    {
        Redacted redacted => CondensedPlan.From(redacted.Plan),
        Refused refused => throw new InvalidOperationException($"Refused: {refused.Rule} at {refused.Location}"),
    };

    private static JsonObject Resources(CondensedPlan plan) =>
        JsonNode.Parse(plan.ToCanonicalJson())!["resource_changes"]!.AsObject();

    private static JsonNode Change(CondensedPlan plan) => Resources(plan).Single().Value!["change"]!;

    private static string Plan(params string[] resources) =>
        $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', resources)}}]}""";

    private static string Resource(
        string address, string actions, string before, string after, string unknown = "{}", string extra = "", bool inChange = true)
    {
        var name = System.Text.Json.JsonSerializer.Serialize(address);
        var marks = $$"""
            "before_sensitive":{{(before == "null" ? "false" : "{}")}},"after_sensitive":{{(after == "null" ? "false" : "{}")}}
            """;
        var changeExtra = inChange ? extra : string.Empty;
        var resourceExtra = inChange ? string.Empty : extra;
        return $$$"""
            {"address":{{{name}}},{{{resourceExtra}}}"type":"x","change":{{{{changeExtra}}}"actions":{{{actions}}},
            "before":{{{before}}},"after":{{{after}}},"after_unknown":{{{unknown}}},{{{marks}}} }}
            """;
    }
}
