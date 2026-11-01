using TfReview.Core;
using System.Diagnostics;

namespace TfReview.Scanners.Tests;

/// <summary>
/// Runs the real conftest over the real tf-standards, from TF_STANDARDS_DIR. CI always provides both, so
/// a skip can't hide a failure there; locally these skip if the directory isn't set.
/// </summary>
public class ConftestRunnerTests
{
    private const string C5MissingTags = """
        {"address":"azurerm_virtual_network.c5","mode":"managed","type":"azurerm_virtual_network","name":"c5","change":{
        "actions":["create"],"before":null,"after":{"name":"vnet-demo-prod","tags":null},
        "after_unknown":{},"before_sensitive":false,"after_sensitive":{}}}
        """;

    private const string C6BadName = """
        {"address":"azurerm_virtual_network.c6","mode":"managed","type":"azurerm_virtual_network","name":"c6","change":{
        "actions":["create"],"before":null,"after":{"name":"BadName","tags":{"owner":"a","environment":"prod","project":"b"}},
        "after_unknown":{},"before_sensitive":false,"after_sensitive":{}}}
        """;

    private const string C8TagValue = """
        {"address":"azurerm_virtual_network.c8","mode":"managed","type":"azurerm_virtual_network","name":"c8","change":{
        "actions":["update"],
        "before":{"name":"vnet-demo-prod","location":"germanywestcentral","tags":{"owner":"a","environment":"prod","project":"b"}},
        "after":{"name":"vnet-demo-prod","location":"germanywestcentral","tags":{"owner":"c","environment":"prod","project":"b"}},
        "after_unknown":{},"before_sensitive":{"tags":{}},"after_sensitive":{"tags":{}}}}
        """;

    private const string StorageCompliant = """
        {"address":"azurerm_storage_account.ok","mode":"managed","type":"azurerm_storage_account","name":"ok","change":{
        "actions":["create"],"before":null,"after":{"name":"stdemoprod","allow_nested_items_to_be_public":false,
        "https_traffic_only_enabled":true,"min_tls_version":"TLS1_2","tags":{"owner":"a","environment":"prod","project":"b"},
        "primary_access_key":"should-be-redacted-value"},
        "after_unknown":{},"before_sensitive":false,"after_sensitive":{"primary_access_key":true}}}
        """;

    [Test]
    public async Task Finds_missing_tags_on_a_redacted_plan()
    {
        var violations = await Violations(C5MissingTags);

        await Assert.That(violations.Count).IsEqualTo(3);
        await Assert.That(violations.All(violation => violation.DocId == "ADR-002")).IsTrue();
    }

    [Test]
    public async Task Finds_a_bad_name_on_a_redacted_plan()
    {
        var violations = await Violations(C6BadName);

        await Assert.That(violations.Single().DocId).IsEqualTo("ADR-001");
    }

    [Test]
    public async Task Stays_clean_on_a_tag_value_update()
    {
        await Assert.That((await Violations(C8TagValue)).Count).IsEqualTo(0);
    }

    // Redaction must not change what the policies decide, including for a resource with a redacted attribute.
    [Test]
    [Arguments(C5MissingTags)]
    [Arguments(C6BadName)]
    [Arguments(C8TagValue)]
    [Arguments(StorageCompliant)]
    public async Task Policies_decide_the_same_on_raw_and_redacted_plans(string resource)
    {
        var standards = Standards();
        var redacted = await Violations(resource);

        var directory = Directory.CreateTempSubdirectory("tfreview-raw-");
        try
        {
            var path = Path.Combine(directory.FullName, "plan.json");
            await File.WriteAllTextAsync(path, Plan(resource));
            var raw = await new ProcessRunner().RunAsync(
                "conftest",
                ["test", "--no-color", "--all-namespaces", "--output", "json", "--parser", "json", "--policy", standards, path],
                TimeSpan.FromMinutes(1));

            var rawViolations = ((ConftestRan)ConftestRunner.Parse(raw!.Output)).Violations;

            await Assert.That(redacted.Select(violation => (violation.DocId, violation.Address, violation.Severity)).ToArray())
                .IsEquivalentTo(rawViolations.Select(violation => (violation.DocId, violation.Address, violation.Severity)).ToArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_missing_policy_directory_is_not_clean()
    {
        Standards();
        var outcome = await new ConftestRunner(new ProcessRunner()).RunAsync(Redact(Plan(C6BadName)), "/nonexistent-policies");

        await Assert.That(outcome).IsEqualTo(new ConftestFailed(ConftestFailure.NoJson));
    }

    [Test]
    public async Task A_missing_conftest_is_not_clean()
    {
        var outcome = await new ConftestRunner(new ProcessRunner(), "conftest-does-not-exist")
            .RunAsync(Redact(Plan(C6BadName)), "/nonexistent-policies");

        await Assert.That(outcome).IsEqualTo(new ConftestFailed(ConftestFailure.CouldNotRun));
    }

    private static async Task<IReadOnlyList<ConftestViolation>> Violations(string resource)
    {
        var outcome = await new ConftestRunner(new ProcessRunner()).RunAsync(Redact(Plan(resource)), Standards());
        return outcome is ConftestRan ran
            ? ran.Violations
            : throw new InvalidOperationException($"conftest didn't run: {outcome}");
    }

    private static string Standards()
    {
        var directory = Environment.GetEnvironmentVariable("TF_STANDARDS_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            return Path.GetFullPath(directory);
        }

        if (Environment.GetEnvironmentVariable("CI") is not null)
        {
            throw new InvalidOperationException("TF_STANDARDS_DIR must be set in CI.");
        }

        Skip.Test("TF_STANDARDS_DIR isn't set.");
        throw new UnreachableException();
    }

    private static string Plan(string resource) => $$"""{"format_version":"1.2","resource_changes":[{{resource}}]}""";

    private static RedactedPlan Redact(string plan) => ((Redacted)new PlanRedactor().Redact(plan)).Plan;
}
