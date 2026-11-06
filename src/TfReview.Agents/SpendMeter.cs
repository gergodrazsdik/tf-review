using Microsoft.Extensions.AI;
using TfReview.Core;

namespace TfReview.Agents;

/// <summary>
/// Reports what each model call is estimated to cost (<see cref="ModelPrices"/>), for the daily budget (D62). It sits
/// on the model itself, under the result cache, so an answer from the cache costs nothing, and a call that fails after
/// the model answered is still counted.
/// </summary>
/// <param name="inner">The model.</param>
/// <param name="deployment">The deployment, which prices are keyed by.</param>
/// <param name="spent">Receives each call's estimated cost in US dollars.</param>
public sealed class SpendMeter(IChatClient inner, string deployment, Action<decimal> spent) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        if (response.Usage is { } usage
            && ModelPrices.Estimate(deployment, usage.InputTokenCount ?? 0, usage.CachedInputTokenCount ?? 0, usage.OutputTokenCount ?? 0) is { } cost)
        {
            spent(cost);
        }

        return response;
    }
}
