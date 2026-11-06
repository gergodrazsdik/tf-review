using System.Text.Json;
using Json.Schema;
using TfReview.Core;

namespace TfReview.Eval;

/// <summary>
/// Checks a real review's result, findings included, against docs/contracts/review-result.schema.json, which refers to
/// finding.schema.json. The contract examples leave out most optional fields, so only real results show drift in them.
/// </summary>
internal sealed class Contracts
{
    private readonly JsonSchema result;

    public Contracts(string directory)
    {
        // review-result.schema.json has no $id, so its $ref to finding.schema.json resolves to the file beside it.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var findingPath = Path.GetFullPath(Path.Combine(directory, "finding.schema.json"));
        options.SchemaRegistry.Register(new Uri(findingPath), JsonSchema.FromFile(findingPath, options));
        this.result = JsonSchema.FromFile(Path.GetFullPath(Path.Combine(directory, "review-result.schema.json")), options);
    }

    /// <summary>Where the serialized result breaks the contract, or nothing.</summary>
    public IReadOnlyList<string> Problems(ReviewResult review)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(review));
        var evaluation = this.result.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid)
        {
            return [];
        }

        return
        [
            .. (evaluation.Details ?? [])
                .Where(detail => detail.Errors is { Count: > 0 })
                .SelectMany(detail => detail.Errors!.Select(error => $"contract: {detail.InstanceLocation} {error.Key}: {error.Value}"))
                .DefaultIfEmpty("contract: the result doesn't match review-result.schema.json"),
        ];
    }
}
