using Microsoft.Extensions.AI;

namespace TfReview.Agents;

/// <summary>
/// One review's time for the model (D54's partial result). Past <paramref name="stopStarting"/> no new model call or
/// batch starts, and at <paramref name="stopCalls"/> the calls still running are cut off as timed out. What's left is
/// reported as not reviewed, so the review still posts what it has before the job's own deadline, which would otherwise
/// cancel everything and leave the message to retry.
/// </summary>
/// <param name="stopStarting">From now, how long new calls may start.</param>
/// <param name="stopCalls">From now, when running calls are cut off.</param>
/// <param name="time">The clock, for tests.</param>
public sealed class ReviewClock(TimeSpan stopStarting, TimeSpan stopCalls, TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly DateTimeOffset start = (time ?? TimeProvider.System).GetUtcNow();
    private readonly CancellationTokenSource cutOff = new(stopCalls, time ?? TimeProvider.System);

    /// <summary>Whether it's too late to start another call or batch.</summary>
    public bool Expired => this.time.GetUtcNow() - this.start >= stopStarting;

    /// <summary>Cancelled when running calls are cut off.</summary>
    public CancellationToken CutOff => this.cutOff.Token;

    public void Dispose() => this.cutOff.Dispose();
}

/// <summary>Gives the model calls under it the review's clock, which they find as a service.</summary>
public sealed class ClockedChat(IChatClient inner, ReviewClock clock) : DelegatingChatClient(inner)
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ReviewClock) ? clock : base.GetService(serviceType, serviceKey);
}
