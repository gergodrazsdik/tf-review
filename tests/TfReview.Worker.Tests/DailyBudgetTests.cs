using Microsoft.Extensions.AI;
using TfReview.Agents;
using TfReview.Service;

namespace TfReview.Worker.Tests;

/// <summary>The hard daily stop on model spend (D62, SEC-5).</summary>
public class DailyBudgetTests
{
    [Test]
    [Arguments(null, 5)]
    [Arguments("", 5)]
    [Arguments("2.50", 2.5)]
    [Arguments("0", 5)]
    [Arguments("-1", 5)]
    [Arguments("lots", 5)]
    public async Task The_limit_comes_from_the_variable_when_its_a_positive_number(string? value, double expected)
    {
        await Assert.That(DailyBudget.LimitFrom(value)).IsEqualTo((decimal)expected);
    }

    [Test]
    public async Task Spend_counts_once_recorded_and_stops_the_day_at_the_limit()
    {
        var counters = new MemoryCounters();
        var budget = new DailyBudget(counters, 1m);

        budget.Spend(0.6m);
        await Assert.That(await budget.ReachedAsync(CancellationToken.None)).IsFalse();

        await budget.RecordAsync(CancellationToken.None);
        budget.Spend(0.5m);
        await budget.RecordAsync(CancellationToken.None);
        await budget.RecordAsync(CancellationToken.None);

        await Assert.That(await budget.ReachedAsync(CancellationToken.None)).IsTrue();
        await Assert.That(counters.Adds).IsEqualTo(2);
    }

    // Jobs are separate processes, so what one spent has to reach the next through the store.
    [Test]
    public async Task Another_execution_sees_the_days_spend()
    {
        var counters = new MemoryCounters();
        var first = new DailyBudget(counters, 1m);
        first.Spend(1.2m);
        await first.RecordAsync(CancellationToken.None);

        await Assert.That(await new DailyBudget(counters, 1m).ReachedAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task A_model_call_reports_its_estimated_cost()
    {
        var spent = new List<decimal>();
        var answer = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 0 },
        };
        using var meter = new SpendMeter(new OneAnswer(answer), "gpt-6.1-sol", spent.Add);

        await meter.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        await Assert.That(spent.Single()).IsEqualTo(2.40m);
    }

    private sealed class MemoryCounters : ICounterStore
    {
        private readonly Dictionary<(string, string), double> values = [];

        public int Adds { get; private set; }

        public Task<double> ReadAsync(string partition, string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.values.GetValueOrDefault((partition, id)));

        public Task<double> AddAsync(string partition, string id, double amount, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        {
            this.Adds++;
            var value = this.values.GetValueOrDefault((partition, id)) + amount;
            this.values[(partition, id)] = value;
            return Task.FromResult(value);
        }
    }

    private sealed class OneAnswer(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
