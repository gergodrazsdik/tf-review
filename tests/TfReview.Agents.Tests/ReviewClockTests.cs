using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

/// <summary>
/// A review's own time (BUG-5, OPS-13): past it nothing new starts and what's left is reported, so the review posts a
/// partial result (D54) instead of the job's deadline cancelling all of it.
/// </summary>
public class ReviewClockTests
{
    private const string Empty = """{"policy_suggestions":[],"findings":[]}""";

    private static readonly StandardsDoc Tagging = TestPlans.Doc("ADR-002", "*", Severity.Medium, "Tags body.");

    // About 8K estimated tokens each, so two fit in a call and a third doesn't.
    private static string Big(string address) =>
        TestPlans.Create(address, "azurerm_virtual_network", $$"""{"name":"{{new string('x', 25_000)}}"}""");

    [Test]
    public async Task Batches_past_the_reviews_time_are_reported_as_timed_out()
    {
        var addresses = Enumerable.Range(0, 4).Select(index => $"azurerm_virtual_network.n{index}").ToList();
        var plan = TestPlans.Condensed([.. addresses.Select(Big)]);
        var time = new ManualTime();
        using var clock = new ReviewClock(TimeSpan.FromMinutes(9), TimeSpan.FromHours(1), time);
        var model = new FakeChat(FakeChat.Answer(Empty), FakeChat.Answer(Empty));
        using var chat = new ClockedChat(new Advancing(model, time, TimeSpan.FromMinutes(10)), clock);

        var outcome = (AgentReviewed)await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(model.Requests.Count).IsEqualTo(1);
        await Assert.That(outcome.Skipped.Single().Reason).IsEqualTo(NotReviewedReason.Timeout);
        await Assert.That(outcome.Skipped.Single().Resources.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_call_past_the_reviews_time_isnt_made()
    {
        var plan = TestPlans.Condensed(TestPlans.Create("azurerm_virtual_network.a", "azurerm_virtual_network", """{"name":"a"}"""));
        var time = new ManualTime();
        using var clock = new ReviewClock(TimeSpan.FromMinutes(9), TimeSpan.FromHours(1), time);
        time.Now += TimeSpan.FromMinutes(9);
        var model = new FakeChat(FakeChat.Answer(Empty));
        using var chat = new ClockedChat(model, clock);

        var outcome = await new ReviewAgent(chat, AgentProfile.Conventions).ReviewAsync(plan, [Tagging], []);

        await Assert.That(model.Requests).IsEmpty();
        await Assert.That(((AgentFailed)outcome).Reason).IsEqualTo(NotReviewedReason.Timeout);
    }

    // The cut-off isn't the caller cancelling, so it ends the call as a timeout rather than throwing.
    [Test]
    public async Task The_cut_off_ends_a_running_call_as_timed_out()
    {
        var plan = TestPlans.Condensed(TestPlans.Create("azurerm_virtual_network.a", "azurerm_virtual_network", """{"name":"a"}"""));
        using var clock = new ReviewClock(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(200));
        using var chat = new ClockedChat(new FakeChat(hang: true), clock);

        var outcome = await new ReviewAgent(chat, AgentProfile.Conventions, TimeSpan.FromMinutes(5)).ReviewAsync(plan, [Tagging], []);

        await Assert.That(((AgentFailed)outcome).Reason).IsEqualTo(NotReviewedReason.Timeout);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    /// <summary>Moves the clock on after each answer, as a slow model would.</summary>
    private sealed class Advancing(IChatClient inner, ManualTime time, TimeSpan step) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            time.Now += step;
            return response;
        }
    }
}
