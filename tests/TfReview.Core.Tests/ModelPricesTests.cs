namespace TfReview.Core.Tests;

/// <summary>The list prices the cost estimates use, so a model comparison isn't skewed by a typo.</summary>
public class ModelPricesTests
{
    [Test]
    [Arguments("gpt-5.4-mini", "0.825", "0.0825", "4.95")]
    [Arguments("gpt-6.1-sol", "2.40", "0.12", "12.00")]
    [Arguments("gpt-6-luna", "0.12", "0.012", "0.60")]
    public async Task Charges_each_kind_of_token_at_its_rate(string model, string input, string cached, string output)
    {
        // A million of each: 2M input of which 1M cached, and 1M output.
        var cost = ModelPrices.Estimate(model, 2_000_000, 1_000_000, 1_000_000);

        await Assert.That(cost).IsEqualTo(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)
            + decimal.Parse(cached, System.Globalization.CultureInfo.InvariantCulture)
            + decimal.Parse(output, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task An_unknown_model_has_no_estimate()
    {
        await Assert.That(ModelPrices.Estimate("gpt-unknown", 1, 0, 1)).IsNull();
    }
}
