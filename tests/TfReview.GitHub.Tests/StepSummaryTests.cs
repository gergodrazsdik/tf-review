using TfReview.Core;

namespace TfReview.GitHub.Tests;

public class StepSummaryTests
{
    private static readonly string Commit = new('a', 40);

    [Test]
    public async Task Shows_tokens_cost_and_time()
    {
        var usage = new TokenUsage(1_000_000, 100_000) { CachedInputTokens = 0, Model = "gpt-5.4-mini", Calls = 2, LatencyMs = 2500 };

        var summary = StepSummary.Render(Result() with { Usage = usage });

        await Assert.That(summary).Contains("| `gpt-5.4-mini` | 2 | 1000000 | 0 | 100000 | ~$1.3200 | 2.5 s |");
    }

    [Test]
    public async Task Cached_tokens_cost_less()
    {
        var usage = new TokenUsage(1_000_000, 0) { CachedInputTokens = 1_000_000, Model = "gpt-5.4-mini" };

        await Assert.That(StepSummary.EstimateCost(usage)).IsEqualTo(0.0825m);
    }

    // An invented price would be worse than none.
    [Test]
    public async Task Doesnt_guess_the_price_of_an_unknown_model()
    {
        var usage = new TokenUsage(100, 100) { Model = "some-other-model", Calls = 1 };

        await Assert.That(StepSummary.EstimateCost(usage)).IsNull();
        await Assert.That(StepSummary.Render(Result() with { Usage = usage })).Contains("| unknown |");
    }

    [Test]
    public async Task Says_when_no_model_was_called()
    {
        await Assert.That(StepSummary.Render(Result())).Contains("No model calls.");
    }

    [Test]
    public async Task Lists_evidence_drops_by_reason()
    {
        var result = Result() with { EvidenceDrops = new Dictionary<string, int> { ["pointer_unresolved"] = 2 } };

        await Assert.That(StepSummary.Render(result)).Contains("- `pointer_unresolved`: 2");
    }

    // The summary is public, and titles quote plan values.
    [Test]
    public async Task Holds_no_finding_text()
    {
        Finding finding = new("a.secret-name", "ADR-002", Severity.Low, Reporter.Scanner, true, "plan text", [new PlanEvidence("/resource_changes/a.b")]);

        var summary = StepSummary.Render(Result() with { Verdict = Verdict.Findings, Findings = [finding] });

        await Assert.That(summary).Contains("1 finding(s)");
        await Assert.That(summary).DoesNotContain("plan text");
        await Assert.That(summary).DoesNotContain("secret-name");
    }

    private static ReviewResult Result() => new(1, Commit, Verdict.NoFindings, [], [], new Dictionary<string, int>());
}
