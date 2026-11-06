using TfReview.Core;

namespace TfReview.Agents.Tests;

/// <summary>One review's calls kept under the TPM limit by waiting, not by 429 retries (D54).</summary>
public class TokenPacerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    // MNT-11, OPS-5: several jobs share one deployment, so each review's budget comes from infra.
    [Test]
    [Arguments(null, null)]
    [Arguments("", null)]
    [Arguments("30000", 30_000)]
    [Arguments("1000", null)]
    [Arguments("-30000", null)]
    [Arguments("lots", null)]
    public async Task A_budget_comes_from_the_variable_when_its_a_sensible_number(string? value, int? expected)
    {
        await Assert.That(TokenPacer.ParseBudget(value)).IsEqualTo(expected);
    }

    // BUG-10: the fallback model has half the capacity, and pacing it at the main one's budget brings back the 429s.
    [Test]
    public async Task The_fallback_model_gets_its_own_default()
    {
        await Assert.That(new ReviewModel(new FakeChat(), "gpt-5.4-mini").TokensPerMinute).IsEqualTo(TokenPacer.FallbackTokensPerMinute);
        await Assert.That(new ReviewModel(new FakeChat(), "gpt-6.1-sol").TokensPerMinute).IsEqualTo(TokenPacer.DefaultTokensPerMinute);
    }

    [Test]
    public async Task A_call_that_fits_goes_now()
    {
        var pacer = new TokenPacer(new FakeChat(), tokensPerMinute: 100);
        pacer.Reserve(Start, 60);

        await Assert.That(pacer.DelayFor(Start.AddSeconds(10), 40)).IsNull();
    }

    [Test]
    public async Task A_call_that_doesnt_fit_waits_for_the_oldest_to_leave_the_window()
    {
        var pacer = new TokenPacer(new FakeChat(), tokensPerMinute: 100);
        pacer.Reserve(Start, 60);
        pacer.Reserve(Start.AddSeconds(20), 30);

        await Assert.That(pacer.DelayFor(Start.AddSeconds(25), 20)).IsEqualTo(TimeSpan.FromSeconds(35));
    }

    [Test]
    public async Task Calls_older_than_a_minute_no_longer_count()
    {
        var pacer = new TokenPacer(new FakeChat(), tokensPerMinute: 100);
        pacer.Reserve(Start, 90);

        await Assert.That(pacer.DelayFor(Start.AddMinutes(1), 90)).IsNull();
    }

    // Waiting can't make it fit, so it goes once nothing else is in the window.
    [Test]
    public async Task A_call_larger_than_the_budget_goes_when_the_window_is_empty()
    {
        var pacer = new TokenPacer(new FakeChat(), tokensPerMinute: 100);

        await Assert.That(pacer.DelayFor(Start, 500)).IsNull();
    }

    [Test]
    public async Task A_call_reserves_its_prompt_and_its_output_limit()
    {
        var pacer = new TokenPacer(new FakeChat(FakeChat.Answer("""{"resources":[]}""")), tokensPerMinute: 4_000 + 10_000);
        var plan = TestPlans.Condensed(TestPlans.Create("a.first", "a", "{}"), TestPlans.Create("a.second", "a", "{}"));

        await new RiskOrchestrator(pacer).RankAsync(plan);

        // StructuredCall.MaxOutputTokens (4,000) is now taken from a budget only 10K above it, so the prompt was counted too.
        await Assert.That(pacer.DelayFor(DateTimeOffset.UtcNow, 10_000)).IsNotNull();
        await Assert.That(pacer.DelayFor(DateTimeOffset.UtcNow, 1)).IsNull();
    }
}
