namespace TfReview.Api;

/// <summary>
/// Counts requests with a token that isn't valid, and logs the count at most once a period. Anyone can send one, so a
/// line each would let a flood fill the workspace's 0.2 GB daily cap and blind the dashboard for the day (SEC-7).
/// </summary>
/// <param name="write">Writes one line with the count since the last.</param>
/// <param name="period">How often a count is written at most.</param>
/// <param name="time">The clock, for tests.</param>
public sealed class RefusalLog(Action<long> write, TimeSpan period, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private long count;
    private DateTimeOffset? last;

    /// <summary>Notes one refusal, and writes the count when a period has passed since the last line.</summary>
    public void Refused()
    {
        long total;
        lock (this.gate)
        {
            this.count++;
            var now = this.time.GetUtcNow();
            if (this.last is { } previous && now - previous < period)
            {
                return;
            }

            total = this.count;
            this.count = 0;
            this.last = now;
        }

        write(total);
    }
}
