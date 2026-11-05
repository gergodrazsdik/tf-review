using Microsoft.Extensions.AI;

namespace TfReview.Agents;

/// <summary>
/// Keeps one review's model calls under the deployment's tokens-per-minute limit (D54), by waiting before a call rather
/// than retrying after a 429. Azure counts a request against the limit as its prompt plus its max_tokens, so that is what
/// each call reserves. It only sees its own review: two large reviews at once can still meet the limit, which the SDK's
/// retries then absorb.
/// </summary>
/// <param name="inner">The model.</param>
/// <param name="tokensPerMinute">The budget, below the deployment's capacity.</param>
/// <param name="time">The clock, for tests.</param>
public sealed class TokenPacer(IChatClient inner, int tokensPerMinute = TokenPacer.DefaultTokensPerMinute, TimeProvider? time = null)
    : DelegatingChatClient(inner)
{
    /// <summary>
    /// 90% of the deployment's 100K capacity in infra/main.tf, which leaves room for the estimate being low and for
    /// another review running at the same time. Change it with the capacity.
    /// </summary>
    public const int DefaultTokensPerMinute = 90_000;

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
