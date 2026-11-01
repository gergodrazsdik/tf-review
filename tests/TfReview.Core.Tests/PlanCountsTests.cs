namespace TfReview.Core.Tests;

public class PlanCountsTests
{
    [Test]
    public async Task Counts_changes_by_action_and_leaves_no_ops_out_of_the_total()
    {
        var counts = Count("""["create"]""", """["create"]""", """["no-op"]""", """["delete","create"]""", """["forget"]""");

        await Assert.That(counts.ResourceChanges).IsEqualTo(4);
        await Assert.That(counts.Actions["create"]).IsEqualTo(2);
        await Assert.That(counts.Actions["no-op"]).IsEqualTo(1);
        await Assert.That(counts.Actions["replace"]).IsEqualTo(1);
        await Assert.That(counts.Actions["other"]).IsEqualTo(1);
    }

    [Test]
    public async Task An_empty_plan_counts_as_nothing()
    {
        await Assert.That(Count().ResourceChanges).IsEqualTo(0);
    }

    private static PlanCounts Count(params string[] actions)
    {
        var changes = actions.Select((action, i) => $$$"""
            {"address":"a.r{{{i}}}","change":{"actions":{{{action}}},"before":null,"after":null,"after_unknown":{},
            "before_sensitive":false,"after_sensitive":false} }
            """);
        var plan = $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', changes)}}]}""";
        return PlanCounts.From(((Redacted)new PlanRedactor().Redact(plan)).Plan);
    }
}
