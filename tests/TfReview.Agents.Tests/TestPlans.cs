using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

/// <summary>Builds plans and documents through the real redactor and condenser, the only way agents get them.</summary>
internal static class TestPlans
{
    public static string Create(string address, string type, string after) =>
        $$"""
        {"address":"{{address}}","type":"{{type}}","mode":"managed","change":{"actions":["create"],"before":null,"after":{{after}},"after_unknown":{},"before_sensitive":false,"after_sensitive":{} } }
        """;

    public static string Json(params string[] resources) =>
        $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', resources)}}]}""";

    public static CondensedPlan Condensed(params string[] resources) =>
        new PlanRedactor().Redact(Json(resources)) is Redacted redacted
            ? CondensedPlan.From(redacted.Plan)
            : throw new InvalidOperationException("The test plan was refused.");

    public static StandardsDoc Doc(string id, string appliesTo, Severity severity = Severity.Medium, string body = "Text.") =>
        new(id, StandardsDoc.Accepted, severity, [appliesTo], ["tagging"], $"adr/{id}.md", body);
}
