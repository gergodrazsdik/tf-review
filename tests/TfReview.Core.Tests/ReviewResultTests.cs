using System.Text.Json;
using System.Text.Json.Nodes;

namespace TfReview.Core.Tests;

public class ReviewResultTests
{
    [Test]
    public async Task Round_trips_the_contract_example()
    {
        var json = await File.ReadAllTextAsync("contracts/review-result.json");

        var result = JsonSerializer.Deserialize<ReviewResult>(json)!;

        await Assert.That(result.Verdict).IsEqualTo(Verdict.Findings);
        await Assert.That(result.Findings[0].Agent).IsEqualTo(Reporter.Conventions);
        await Assert.That(JsonNode.DeepEquals(
            JsonNode.Parse(json), JsonNode.Parse(JsonSerializer.Serialize(result)))).IsTrue();
    }

    [Test]
    [Arguments("""{"version":1,"commit":"a","verdict":"no_findings","findings":[],"evidence_drops":{}}""")]
    [Arguments("""{"version":1,"commit":"a","verdict":"unknown","findings":[],"not_reviewed":[],"evidence_drops":{}}""")]
    [Arguments("not json")]
    public async Task Rejects_a_malformed_result(string json)
    {
        await Assert.That(() => ReviewResult.Parse(json)).Throws<JsonException>();
    }

    [Test]
    public async Task A_failed_review_says_why()
    {
        var result = ReviewResult.Failed(new string('a', 40), ReviewScope.Plan, NotReviewedReason.Timeout);

        var json = JsonNode.Parse(JsonSerializer.Serialize(result))!;

        await Assert.That(json["verdict"]!.GetValue<string>()).IsEqualTo("not_reviewed");
        await Assert.That(json["findings"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(json["not_reviewed"]![0]!["reason"]!.GetValue<string>()).IsEqualTo("timeout");
    }
}
