using System.Globalization;
using TfReview.Service;

namespace TfReview.Worker;

/// <summary>Whether the model may still be called today, and where a review's spend is recorded.</summary>
public interface IModelBudget
{
    /// <summary>Whether today's recorded spend has reached the limit.</summary>
    Task<bool> ReachedAsync(CancellationToken cancellationToken);

    /// <summary>Adds what this execution spent since the last time to today's total.</summary>
    Task RecordAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A hard daily stop on model spend (D62). The Azure budget only sends emails, on actual cost, a day late, so a runaway
/// could spend the month's budget before anyone reads one (SEC-5, OPS-3). Each review adds its estimated cost, from
/// <see cref="TfReview.Core.ModelPrices"/>, to a counter for the day (UTC) in the reviews container, and a review that
/// finds the day's total at the limit says "not reviewed" without calling the model. One review can pass the limit by
/// its own cost, because it's checked before a review, not between calls.
/// </summary>
/// <param name="counters">Where the day's total is kept.</param>
/// <param name="limitUsd">The day's limit in US dollars.</param>
/// <param name="time">The clock, for tests.</param>
public sealed class DailyBudget(ICounterStore counters, decimal limitUsd, TimeProvider? time = null) : IModelBudget
{
    /// <summary>The variable holding the daily limit in US dollars.</summary>
    public const string Variable = "TF_REVIEW_DAILY_BUDGET_USD";

    /// <summary>
    /// The limit when the variable isn't set: about five times a busy day of reviews, and well under a month's €30
    /// budget, so a runaway stops within the day.
    /// </summary>
    public const decimal DefaultUsd = 5m;

    // Not a repo's ID, which is a number, so it can't share a partition with one.
    private const string Partition = "tf-review-spend";

    private static readonly TimeSpan Life = TimeSpan.FromDays(3);

    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private decimal pending;

    public decimal LimitUsd => limitUsd;

    /// <summary>The limit from the variable, or the default when it's unset or not a positive number.</summary>
    public static decimal LimitFrom(string? value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var limit) && limit > 0 ? limit : DefaultUsd;

    /// <summary>Notes one call's estimated cost, which <see cref="RecordAsync"/> adds to the day's total.</summary>
    public void Spend(decimal usd)
    {
        lock (this.gate)
        {
            this.pending += usd;
        }
    }

    public async Task<bool> ReachedAsync(CancellationToken cancellationToken) =>
        (decimal)await counters.ReadAsync(Partition, this.Today(), cancellationToken) >= limitUsd;

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        decimal amount;
        lock (this.gate)
        {
            amount = this.pending;
            this.pending = 0;
        }

        if (amount <= 0)
        {
            return;
        }

        try
        {
            await counters.AddAsync(Partition, this.Today(), (double)amount, Life, cancellationToken);
        }
        catch
        {
            // Kept for the next try, such as the give-up's.
            this.Spend(amount);
            throw;
        }
    }

    private string Today() => $"spend-{this.time.GetUtcNow():yyyy-MM-dd}";
}
