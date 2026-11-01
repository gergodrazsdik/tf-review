using System.Text.Json;
using System.Text.Json.Nodes;

namespace TfReview.Core.Tests;

public class EvidenceTests
{
    [Test]
    public async Task Round_trips_the_contract_example()
    {
        var example = JsonNode.Parse(await File.ReadAllTextAsync("contracts/finding.json"))!;
        var json = example["evidence"]!.ToJsonString();

        var evidence = JsonSerializer.Deserialize<Evidence[]>(json)!;

        await Assert.That(evidence[0].Value).IsTypeOf<PlanEvidence>();
        await Assert.That(evidence[1].Value).IsTypeOf<DocEvidence>();
        await Assert.That(JsonSerializer.Serialize(evidence)).IsEqualTo(json);
    }

    [Test]
    public async Task Rejects_evidence_of_an_unknown_shape()
    {
        await Assert.That(() => JsonSerializer.Deserialize<Evidence>("""{"url":"https://example.com"}"""))
            .Throws<JsonException>();
    }
}
