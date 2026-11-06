using Microsoft.Extensions.AI;

namespace TfReview.Agents;

/// <summary>
/// Keeps one review's model calls under the deployment's tokens-per-minute limit (D54), by waiting before a call rather
/// than retrying after a 429. Azure counts a request against the limit as its prompt plus its max_tokens, so that is what
/// each call reserves. It only sees its own review, so with several jobs at once each review's budget has to be its share
/// of the capacity, which <c>TF_REVIEW_TOKENS_PER_MINUTE</c> sets.
/// </summary>
/// <param name="inner">The model.</param>
/// <param name="tokensPerMinute">The budget, below the deployment's capacity.</param>
/// <param name="time">The clock, for tests.</param>
public sealed class TokenPacer(IChatClient inner, int tokensPerMinute = TokenPacer.DefaultTokensPerMinute, TimeProvider? time = null)
    : DelegatingChatClient(inner)
{
    /// <summary>
    /// 90% of gpt-6.1-sol's 100K capacity in infra/main.tf, which leaves room for the estimate being low. Change it with
    /// the capacity, or set the variable.
    /// </summary>
    public const int DefaultTokensPerMinute = 90_000;

    /// <summary>90% of the fallback gpt-5.4-mini's 50K capacity (D4), so switching to it doesn't bring back 429s.</summary>
    public const int FallbackTokensPerMinute = 45_000;

    /// <summary>The smallest budget the variable may set: below it no agent's batch of 20K plus 4K output would fit.</summary>
    public const int MinTokensPerMinute = 25_000;

    /// <summary>The default budget for a deployment, when the variable doesn't set one.</summary>
    public static int DefaultFor(string deployment) =>
        deployment == "gpt-5.4-mini" ? FallbackTokensPerMinute : DefaultTokensPerMinute;

    /// <summary>A budget from the variable, or null when it's unset or not a whole number of at least the minimum.</summary>
    public static int? ParseBudget(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var budget)
        && budget >= MinTokensPerMinute
            ? budget
            : null;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Queue<(DateTimeOffset At, int Tokens)> reserved = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Waits until the call fits in the last minute's budget, then reserves its tokens.</summary>
    public async Task ReserveAsync(int tokens, CancellationToken cancellationToken)
    {
        // One caller at a time, so two calls can't both see room for one.
        await this.gate.WaitAsync(cancellationToken);
        try
        {
            while (this.DelayFor(this.time.GetUtcNow(), tokens) is { } delay)
            {
                await Task.Delay(delay, this.time, cancellationToken);
            }

            this.reserved.Enqueue((this.time.GetUtcNow(), tokens));
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// How long a call of this size has to wait at this moment, or null if it fits now. A call larger than the whole
    /// budget goes as soon as the window is empty, because waiting longer wouldn't make it fit.
    /// </summary>
    public TimeSpan? DelayFor(DateTimeOffset now, int tokens)
    {
        while (this.reserved.TryPeek(out var oldest) && now - oldest.At >= Window)
        {
            this.reserved.Dequeue();
        }

        var used = this.reserved.Sum(entry => entry.Tokens);
        if (used == 0 || used + tokens <= tokensPerMinute)
        {
            return null;
        }

        // The oldest reservation leaving the window is the soonest anything changes.
        return this.reserved.Peek().At + Window - now;
    }

    /// <summary>Records a call at a given time without waiting, which with DelayFor lets the pacing be checked without a clock.</summary>
    public void Reserve(DateTimeOffset at, int tokens) => this.reserved.Enqueue((at, tokens));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
