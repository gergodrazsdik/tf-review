using System.Text.Json;

namespace TfReview.Core.Tests;

public class PlanCountsTests
{
    private const string Plan = """
        {"resource_changes": [
          {"address": "a.create", "change": {"actions": ["create"], "after": {"password": "hunter2"}}},
          {"address": "a.again", "change": {"actions": ["create"]}},
          {"address": "b.keep", "change": {"actions": ["no-op"]}},
          {"address": "c.swap", "change": {"actions": ["delete", "create"]}},
          {"address": "d.odd", "change": {"actions": ["forget"]}}
        ]}
        """;

    [Test]
    public async Task Counts_changes_by_action()
    {
        using var plan = JsonDocument.Parse(Plan);

        var counts = PlanCounts.FromPlan(plan);

        await Assert.That(counts.ResourceChanges).IsEqualTo(5);
        await Assert.That(counts.Actions["create"]).IsEqualTo(2);
        await Assert.That(counts.Actions["replace"]).IsEqualTo(1);
        await Assert.That(counts.Actions["other"]).IsEqualTo(1);
    }

    [Test]
    public async Task Writes_nothing_from_the_plan_but_counts()
    {
        using var plan = JsonDocument.Parse(Plan);

        var json = JsonSerializer.Serialize(PlanCounts.FromPlan(plan));

        await Assert.That(json).DoesNotContain("hunter2");
        await Assert.That(json).DoesNotContain("a.create");
    }

    [Test]
    public async Task An_empty_plan_counts_as_nothing()
    {
        using var plan = JsonDocument.Parse("{}");

        await Assert.That(PlanCounts.FromPlan(plan).ResourceChanges).IsEqualTo(0);
    }

    [Test]
    public async Task Parses_what_it_writes()
    {
        using var plan = JsonDocument.Parse(Plan);

        var counts = PlanCounts.Parse(JsonSerializer.Serialize(PlanCounts.FromPlan(plan)));

        await Assert.That(counts.Actions["create"]).IsEqualTo(2);
    }

    // The plan job runs PR code, so it could write anything into this file.
    [Test]
    [Arguments("""{"resource_changes":1,"actions":{"<img src=x>":1}}""")]
    [Arguments("""{"resource_changes":1,"actions":{"create":-1}}""")]
    [Arguments("""{"resource_changes":-1,"actions":{}}""")]
    [Arguments("""{"resource_changes":1}""")]
    [Arguments("not json")]
    public async Task Rejects_a_tampered_file(string json)
    {
        await Assert.That(() => PlanCounts.Parse(json)).Throws<JsonException>();
    }
}
