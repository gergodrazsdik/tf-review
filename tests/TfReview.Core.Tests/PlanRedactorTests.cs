using System.Text;
using System.Text.Json.Nodes;

namespace TfReview.Core.Tests;

public class PlanRedactorTests
{
    // Every place a secret reached in a real `tofu show -json`, by how it should be caught.
    private static readonly string[] MustNotSurvive =
    [
        "CANARY-sensitive-variable-default", // marked value, and embedded in an unmarked string
        "CANARY-marked-list-item", // marked element of a list
        "CANARY-unmarked-under-denylisted-name", // no mark, but a secret-like name
        "CANARY-nested-connection", // the same, nested
        "CANARY-sensitive-output", // sensitive output, also in prior_state and configuration
        "CANARY-plain-variable-default", // only in sections that aren't kept
    ];

    // D25's known gap, pinned here so that changing the policy shows up in a test.
    private static readonly string[] SurvivesByDesign =
    [
        "CANARY-list-item", // an unmarked value under an ordinary name
        "CANARY-for-each-key", // part of an address
        "CANARY-literal-in-config", // an unmarked output
        "vnet-demo-prod",
    ];

    private static string RealPlan => File.ReadAllText("plans/canaries.plan.json");

    [Test]
    public async Task The_fixture_really_contains_every_canary()
    {
        foreach (var canary in MustNotSurvive.Concat(SurvivesByDesign))
        {
            await Assert.That(RealPlan).Contains(canary);
        }
    }

    [Test]
    public async Task Removes_every_canary_from_a_real_plan()
    {
        var json = RedactOk(RealPlan);

        foreach (var canary in MustNotSurvive)
        {
            await Assert.That(json).DoesNotContain(canary);
        }
    }

    [Test]
    public async Task Keeps_what_d25_says_passes()
    {
        var json = RedactOk(RealPlan);

        foreach (var value in SurvivesByDesign)
        {
            await Assert.That(json).Contains(value);
        }
    }

    [Test]
    public async Task Keeps_only_the_allowlisted_sections()
    {
        var root = JsonNode.Parse(RedactOk(RealPlan))!.AsObject();

        await Assert.That(root.Select(field => field.Key).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["format_version", "output_changes", "resource_changes"]);
    }

    [Test]
    public async Task Replaces_a_value_embedded_in_an_unmarked_string()
    {
        var json = RedactOk(RealPlan);

        await Assert.That(json).Contains("prefix-[redacted:");
        await Assert.That(json).Contains("]-suffix");
    }

    [Test]
    public async Task Redacting_a_redacted_plan_changes_nothing()
    {
        var once = RedactOk(RealPlan);

        await Assert.That(RedactOk(once)).IsEqualTo(once);
    }

    [Test]
    public async Task The_order_of_keys_in_the_input_doesnt_matter()
    {
        var reversed = Reverse(JsonNode.Parse(RealPlan))!.ToJsonString();

        await Assert.That(RedactOk(reversed)).IsEqualTo(RedactOk(RealPlan));
    }

    [Test]
    public async Task Adding_a_resource_doesnt_change_anothers_placeholders()
    {
        var plan = JsonNode.Parse(RealPlan)!;
        var before = Resource(RedactOk(RealPlan), "terraform_data.partly_marked");

        var extra = JsonNode.Parse("""
            {"address":"a.first","change":{"actions":["create"],"before":null,"after":{"password":"another-secret-value"},
            "after_unknown":{},"before_sensitive":false,"after_sensitive":{}}}
            """);
        plan["resource_changes"]!.AsArray().Insert(0, extra);

        await Assert.That(Resource(RedactOk(plan.ToJsonString()), "terraform_data.partly_marked")).IsEqualTo(before);
    }

    [Test]
    public async Task An_unchanged_value_gets_the_same_placeholder_on_both_sides()
    {
        var after = JsonNode.Parse(RedactOk(Update("same-secret-value", "same-secret-value")))!;
        var change = after["resource_changes"]![0]!["change"]!;

        await Assert.That(change["before"]!["value"]!.GetValue<string>())
            .IsEqualTo(change["after"]!["value"]!.GetValue<string>());
    }

    [Test]
    public async Task A_changed_value_gets_a_different_placeholder()
    {
        var after = JsonNode.Parse(RedactOk(Update("first-secret-value", "second-secret-value")))!;
        var change = after["resource_changes"]![0]!["change"]!;

        await Assert.That(change["before"]!["value"]!.GetValue<string>())
            .IsNotEqualTo(change["after"]!["value"]!.GetValue<string>());
    }

    [Test]
    public async Task Redacts_the_before_side_too()
    {
        await Assert.That(RedactOk(Update("first-secret-value", "second-secret-value")))
            .DoesNotContain("first-secret-value");
    }

    [Test]
    public async Task Sweeps_base64_and_url_encoded_forms()
    {
        const string Secret = "p@ss word/with+symbols";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret));
        var plan = Plan(
            $$"""
            {"secret":"{{Secret}}","encoded":"{{base64}}","url":"https://x.test/?k={{Uri.EscapeDataString(Secret)}}"}
            """,
            """{"secret":true}""");

        var json = RedactOk(plan);

        await Assert.That(json).DoesNotContain(base64);
        await Assert.That(json).DoesNotContain(Uri.EscapeDataString(Secret));
    }

    [Test]
    public async Task Sweeps_map_keys()
    {
        var plan = Plan("""{"secret":"a-secret-key-name","labels":{"a-secret-key-name":"x"}}""", """{"secret":true}""");

        await Assert.That(RedactOk(plan)).DoesNotContain("a-secret-key-name");
    }

    [Test]
    public async Task Sweeps_values_harvested_from_dropped_sections()
    {
        var plan = JsonNode.Parse(Plan("""{"note":"uses dropped-section-secret here"}""", "{}"))!;
        plan["planned_values"] = JsonNode.Parse("""
            {"root_module":{"child_modules":[{"resources":[
              {"values":{"key":"dropped-section-secret"},"sensitive_values":{"key":true}}]}]}}
            """);

        await Assert.That(RedactOk(plan.ToJsonString())).DoesNotContain("dropped-section-secret");
    }

    [Test]
    public async Task Sweeps_values_from_resource_drift_and_module_configuration()
    {
        var plan = JsonNode.Parse(Plan("""{"a":"drifted-secret-value","b":"module-default-secret"}""", "{}"))!;
        plan["resource_drift"] = JsonNode.Parse("""
            [{"change":{"before":"drifted-secret-value","before_sensitive":true,"after":null,"after_sensitive":false}}]
            """);
        plan["configuration"] = JsonNode.Parse("""
            {"root_module":{"module_calls":{"m":{"module":{"variables":{
              "v":{"sensitive":true,"default":"module-default-secret"}}}}}}}
            """);

        var json = RedactOk(plan.ToJsonString());

        await Assert.That(json).DoesNotContain("drifted-secret-value");
        await Assert.That(json).DoesNotContain("module-default-secret");
    }

    [Test]
    public async Task A_short_marked_value_is_replaced_where_marked_but_not_swept()
    {
        var json = RedactOk(Plan("""{"secret":"prod","name":"vnet-prod"}""", """{"secret":true}"""));

        await Assert.That(json).Contains("vnet-prod");
        await Assert.That(json).DoesNotContain("\"secret\":\"prod\"");
    }

    [Test]
    [Arguments("disable_password_authentication")]
    [Arguments("password_enabled")]
    public async Task Keeps_booleans_under_secret_like_names(string name)
    {
        var json = RedactOk(Plan($$"""{"{{name}}":true}""", "{}"));

        await Assert.That(json).Contains($"\"{name}\":true");
    }

    [Test]
    public async Task Keeps_empty_strings_and_nulls()
    {
        var json = RedactOk(Plan("""{"secret":"","token":null}""", "{}"));

        await Assert.That(json).Contains("\"secret\":\"\"");
        await Assert.That(json).Contains("\"token\":null");
    }

    [Test]
    public async Task Redacts_a_whole_object_under_a_secret_like_name()
    {
        var json = RedactOk(Plan("""{"app_settings":{"KEY":"some-value","OTHER":"another-value"}}""", "{}"));

        await Assert.That(json).DoesNotContain("some-value");
        await Assert.That(json).DoesNotContain("another-value");
    }

    [Test]
    public async Task Redacts_a_marked_number()
    {
        var json = RedactOk(Plan("""{"pin":123456}""", """{"pin":true}"""));

        await Assert.That(json).DoesNotContain("123456");
    }

    [Test]
    public async Task Drops_generated_config()
    {
        var plan = JsonNode.Parse(Plan("""{"a":1}""", "{}"))!;
        plan["resource_changes"]![0]!["change"]!["generated_config"] = "resource \"x\" { key = \"from-import\" }";

        await Assert.That(RedactOk(plan.ToJsonString())).DoesNotContain("from-import");
    }

    [Test]
    [Arguments("not json", RefusalRule.MalformedJson)]
    [Arguments("[]", RefusalRule.MalformedPlan)]
    [Arguments("{}", RefusalRule.UnsupportedFormat)]
    [Arguments("""{"format_version":"2.0"}""", RefusalRule.UnsupportedFormat)]
    [Arguments("""{"format_version":1}""", RefusalRule.UnsupportedFormat)]
    [Arguments("""{"format_version":"1.2","errored":true}""", RefusalRule.ErroredPlan)]
    [Arguments("""{"format_version":"1.2","resource_changes":{}}""", RefusalRule.MalformedPlan)]
    [Arguments("""{"format_version":"1.2","resource_changes":[{}]}""", RefusalRule.MalformedPlan)]
    [Arguments("""{"format_version":"1.2","resource_changes":[{"address":"a.b"}]}""", RefusalRule.MalformedPlan)]
    [Arguments("""{"format_version":"1.2","output_changes":[]}""", RefusalRule.MalformedPlan)]
    [Arguments("""{"format_version":"1.2","a":1,"a":2}""", RefusalRule.MalformedJson)]
    public async Task Refuses_a_malformed_plan(string json, RefusalRule rule)
    {
        var outcome = new PlanRedactor().Redact(json);

        await Assert.That(outcome).IsEqualTo(new Refused(rule, ((Refused)outcome).Location));
    }

    [Test]
    [Arguments("""{"a":"value"}""", """{"a":"not-a-boolean"}""", RefusalRule.MalformedMarks)]
    [Arguments("""{"a":"value"}""", """{"a":{}}""", RefusalRule.MalformedMarks)]
    [Arguments("""{"a":["x","y"]}""", """{"a":[true]}""", RefusalRule.MalformedMarks)]
    [Arguments("""{"a":["x"]}""", """{"a":{}}""", RefusalRule.MalformedMarks)]
    [Arguments("""{"a":{"b":"x"}}""", """{"a":[]}""", RefusalRule.MalformedMarks)]
    public async Task Refuses_marks_that_dont_fit_the_value(string after, string marks, RefusalRule rule)
    {
        var outcome = new PlanRedactor().Redact(Plan(after, marks));

        await Assert.That(outcome is Refused refused && refused.Rule == rule).IsTrue();
    }

    [Test]
    public async Task Refuses_a_value_with_no_marks_at_all()
    {
        var plan = JsonNode.Parse(Plan("""{"a":"value"}""", "{}"))!;
        plan["resource_changes"]![0]!["change"]!.AsObject().Remove("after_sensitive");

        var outcome = new PlanRedactor().Redact(plan.ToJsonString());

        await Assert.That(outcome is Refused { Rule: RefusalRule.MalformedMarks }).IsTrue();
    }

    [Test]
    public async Task Refuses_values_smuggled_through_after_unknown()
    {
        var plan = JsonNode.Parse(Plan("""{"a":1}""", "{}"))!;
        plan["resource_changes"]![0]!["change"]!["after_unknown"] = JsonNode.Parse("""{"a":"smuggled"}""");

        var outcome = new PlanRedactor().Redact(plan.ToJsonString());

        await Assert.That(outcome is Refused { Rule: RefusalRule.MalformedUnknown }).IsTrue();
    }

    [Test]
    public async Task Accepts_an_object_whose_marks_are_simply_false()
    {
        var plan = JsonNode.Parse(Plan("""{"a":{"name":"visible-value"}}""", "false"))!.ToJsonString();

        await Assert.That(RedactOk(plan)).Contains("visible-value");
    }

    [Test]
    public async Task Accepts_an_output_that_is_an_object()
    {
        var plan = """
            {"format_version":"1.2","output_changes":{"o":{"actions":["create"],"before":null,
            "after":{"name":"visible-value"},"after_unknown":false,"before_sensitive":false,"after_sensitive":false}}}
            """;

        await Assert.That(RedactOk(plan)).Contains("visible-value");
    }

    [Test]
    [Arguments("""{"format_version":"1.2","configuration":[]}""")]
    [Arguments("""{"format_version":"1.2","configuration":{"root_module":"x"},"variables":{"v":{"value":"y"}}}""")]
    [Arguments("""{"format_version":"1.2","variables":{"v":"not-an-object"}}""")]
    public async Task Refuses_or_accepts_oddly_shaped_sections_without_crashing(string json)
    {
        var outcome = new PlanRedactor().Redact(json);

        await Assert.That(outcome is Redacted or Refused { Rule: RefusalRule.MalformedPlan }).IsTrue();
    }

    [Test]
    public async Task Refuses_two_changes_to_the_same_resource()
    {
        var plan = JsonNode.Parse(Plan("""{"a":1}""", "{}"))!;
        var changes = plan["resource_changes"]!.AsArray();
        changes.Add(changes[0]!.DeepClone());

        var outcome = new PlanRedactor().Redact(plan.ToJsonString());

        await Assert.That(outcome is Refused { Rule: RefusalRule.MalformedPlan, Location: "/resource_changes/1" }).IsTrue();
    }

    [Test]
    public async Task A_deposed_instance_is_a_separate_change()
    {
        var plan = JsonNode.Parse(Plan("""{"a":1}""", "{}"))!;
        var changes = plan["resource_changes"]!.AsArray();
        var deposed = changes[0]!.DeepClone();
        deposed["deposed"] = "abc123";
        changes.Add(deposed);

        await Assert.That(new PlanRedactor().Redact(plan.ToJsonString()) is Redacted).IsTrue();
    }

    [Test]
    public async Task A_refusal_never_names_an_attribute()
    {
        var outcome = new PlanRedactor().Redact(Plan("""{"my-secret-map-key":"value"}""", """{"my-secret-map-key":"bad"}"""));

        await Assert.That(((Refused)outcome).Location).IsEqualTo("/resource_changes/0");
    }

    // Each layer has to matter: with it off, and the verifier off so nothing catches the mistake, a canary leaks.
    [Test]
    [Arguments("Marks", "CANARY-marked-list-item")]
    [Arguments("Names", "CANARY-unmarked-under-denylisted-name")]
    [Arguments("Sweep", "prefix-CANARY-sensitive-variable-default-suffix")]
    public async Task Each_layer_catches_something_the_others_dont(string layer, string canary)
    {
        var without = RedactionLayers.All & ~Enum.Parse<RedactionLayers>(layer) & ~RedactionLayers.Verifier;

        var outcome = new PlanRedactor(without).Redact(RealPlan);

        await Assert.That(((Redacted)outcome).Plan.ToCanonicalJson()).Contains(canary);
    }

    [Test]
    public async Task The_verifier_refuses_what_the_sweep_missed()
    {
        var outcome = new PlanRedactor(RedactionLayers.All & ~RedactionLayers.Sweep).Redact(RealPlan);

        await Assert.That(outcome is Refused { Rule: RefusalRule.SecretSurvived }).IsTrue();
    }

    private const string SubscriptionId = "12345678-90ab-cdef-1234-567890abcdef";

    private static string PlanWithId(string address = "a.b", string before = "null") => $$"""
        {"address":"{{address}}","type":"a","change":{"actions":["update"],
        "before":{{before}},
        "after":{"id":"/subscriptions/{{SubscriptionId}}/resourceGroups/rg/x","tenant_id":"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE","name":"keep-me"},
        "after_unknown":{},"before_sensitive":{},"after_sensitive":{} } }
        """;

    private static string PlanOf(params string[] resources) =>
        $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', resources)}}]}""";

    private static string RedactedJson(string plan) =>
        ((Redacted)new PlanRedactor().Redact(plan)).Plan.ToCanonicalJson();

    // Subscription IDs sit inside resource IDs, and this plan reaches a public artifact (D30).
    [Test]
    public async Task Masks_guids_inside_values_in_any_case()
    {
        var json = RedactedJson(PlanOf(PlanWithId()));

        await Assert.That(json).DoesNotContain(SubscriptionId);
        await Assert.That(json).DoesNotContain("AAAAAAAA");
        await Assert.That(json).Contains("/subscriptions/[redacted:");
        await Assert.That(json).Contains("/resourceGroups/rg/x");
        await Assert.That(json).Contains("keep-me");
    }

    // Otherwise an unchanged ID would look like a change.
    [Test]
    public async Task An_unchanged_guid_gets_the_same_placeholder_on_both_sides()
    {
        var before = $$"""{"id":"/subscriptions/{{SubscriptionId}}/resourceGroups/rg/x"}""";

        var change = JsonNode.Parse(RedactedJson(PlanOf(PlanWithId(before: before))))!["resource_changes"]![0]!["change"]!;

        await Assert.That(change["before"]!["id"]!.GetValue<string>()).IsEqualTo(change["after"]!["id"]!.GetValue<string>());
    }

    [Test]
    public async Task Masks_a_map_key_and_its_marks_alike()
    {
        var plan = PlanOf($$"""
            {"address":"a.b","type":"a","change":{"actions":["create"],"before":null,
            "after":{"map":{"{{SubscriptionId}}":"secret-value"} },"after_unknown":{},
            "before_sensitive":false,"after_sensitive":{"map":{"{{SubscriptionId}}":true} } } }
            """);

        var json = RedactedJson(plan);

        await Assert.That(json).DoesNotContain(SubscriptionId);
        await Assert.That(json).DoesNotContain("secret-value");
    }

    // Addresses are how findings point at a resource, and two for_each instances must stay distinct.
    [Test]
    public async Task Leaves_a_guid_in_an_address_so_instances_stay_distinct()
    {
        var first = "a.b[\"11111111-1111-1111-1111-111111111111\"]".Replace("\"", "\\\"", StringComparison.Ordinal);
        var second = "a.b[\"22222222-2222-2222-2222-222222222222\"]".Replace("\"", "\\\"", StringComparison.Ordinal);

        var json = RedactedJson(PlanOf(PlanWithId(first), PlanWithId(second)));

        await Assert.That(json).Contains("11111111-1111-1111-1111-111111111111");
        await Assert.That(json).Contains("22222222-2222-2222-2222-222222222222");
    }

    [Test]
    public async Task Masking_guids_is_stable_when_run_again()
    {
        var once = RedactedJson(PlanOf(PlanWithId()));

        await Assert.That(RedactedJson(once)).IsEqualTo(once);
    }

    [Test]
    public async Task Turning_the_guid_layer_off_leaves_them_in()
    {
        var outcome = new PlanRedactor(RedactionLayers.All & ~RedactionLayers.Guids).Redact(PlanOf(PlanWithId()));

        await Assert.That(((Redacted)outcome).Plan.ToCanonicalJson()).Contains(SubscriptionId);
    }

    [Test]
    public async Task Reports_what_it_did_without_any_values()
    {
        var outcome = (Redacted)new PlanRedactor().Redact(RealPlan);

        await Assert.That(outcome.SecretsFound).IsGreaterThan(0);
        await Assert.That(outcome.FieldsDropped).IsGreaterThan(0);
    }

    private static string RedactOk(string plan) => new PlanRedactor().Redact(plan) switch
    {
        Redacted redacted => redacted.Plan.ToCanonicalJson(),
        Refused refused => throw new InvalidOperationException($"Refused: {refused.Rule} at {refused.Location}"),
    };

    private static string Resource(string json, string address) => JsonNode.Parse(json)!["resource_changes"]!.AsArray()
        .Single(change => change!["address"]!.GetValue<string>() == address)!.ToJsonString();

    private static string Plan(string after, string afterSensitive) => $$$"""
        {"format_version":"1.2","resource_changes":[{"address":"a.b","type":"a","name":"b","change":{
        "actions":["update"],"before":null,"after":{{{after}}},"after_unknown":{},
        "before_sensitive":false,"after_sensitive":{{{afterSensitive}}} }}]}
        """;

    private static string Update(string before, string after) => $$$"""
        {"format_version":"1.2","resource_changes":[{"address":"a.b","change":{"actions":["update"],
        "before":{"value":"{{{before}}}"},"after":{"value":"{{{after}}}"},"after_unknown":{},
        "before_sensitive":{"value":true},"after_sensitive":{"value":true} }}]}
        """;

    private static JsonNode? Reverse(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.Reverse()
            .Select(field => KeyValuePair.Create(field.Key, Reverse(field.Value?.DeepClone())))),
        JsonArray value => new JsonArray(value.Select(child => Reverse(child?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}
