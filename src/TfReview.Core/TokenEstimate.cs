namespace TfReview.Core;

/// <summary>
/// A rough token count, for budgets. Three characters per token errs high for English and JSON, so a
/// prompt within a budget here is within it for the model too. Billing uses the model's own counts.
/// </summary>
public static class TokenEstimate
{
    private const int CharactersPerToken = 3;

    public static int Of(string text) => (text.Length + CharactersPerToken - 1) / CharactersPerToken;
}
