using System.Text.Json.Serialization;

namespace TfReview.Core;

/// <summary>A JSON Pointer into the condensed plan.</summary>
public sealed record PlanEvidence([property: JsonPropertyName("plan_pointer"), JsonRequired] string PlanPointer);

/// <summary>An accepted standards document.</summary>
public sealed record DocEvidence([property: JsonPropertyName("doc_id"), JsonRequired] string DocId);

/// <summary>
/// One item of a finding's evidence. The structural classifier tells the cases apart by property
/// name, because the default classifier can't distinguish two JSON objects.
/// </summary>
[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union Evidence(PlanEvidence, DocEvidence);
