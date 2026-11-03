namespace TfReview.Core;

/// <summary>What a model call is estimated to cost.</summary>
public static class ModelPrices
{
    private const decimal TokensPerPrice = 1_000_000m;

    // US dollars per million tokens, from third-party listings of the Data Zone Standard rate, not an invoice.
    private static readonly Dictionary<string, (decimal Input, decimal CachedInput, decimal Output)> Prices = new(StringComparer.Ordinal)
    {
        ["gpt-5.4-mini"] = (0.825m, 0.0825m, 4.95m),
    };

    /// <summary>The estimated cost in US dollars, or null for a model with no known price.</summary>
    public static decimal? Estimate(string? model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (model is null || !Prices.TryGetValue(model, out var price))
        {
            return null;
        }

        var cached = Math.Min(cachedInputTokens, inputTokens);
        var cost = ((inputTokens - cached) * price.Input) + (cached * price.CachedInput) + (outputTokens * price.Output);
        return cost / TokensPerPrice;
    }
}
