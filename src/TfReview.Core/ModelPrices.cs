namespace TfReview.Core;

/// <summary>What a model call is estimated to cost.</summary>
public static class ModelPrices
{
    private const decimal TokensPerPrice = 1_000_000m;

    // US dollars per million tokens, keyed by deployment name: the Data Zone Standard list price in Germany West Central
    // from Azure's Retail Prices API on 2026-11-05, short context for the GPT-6 models, not an invoice. The GPT-6 models
    // also bill cache writes, which this leaves out, so it errs low for them.
    private static readonly Dictionary<string, (decimal Input, decimal CachedInput, decimal Output)> Prices = new(StringComparer.Ordinal)
    {
        ["gpt-5.4-mini"] = (0.825m, 0.0825m, 4.95m),
        ["gpt-6.1-sol"] = (2.40m, 0.12m, 12.00m),
        ["gpt-6-luna"] = (0.12m, 0.012m, 0.60m),
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
