using TfReview.Core;

namespace TfReview.Scanners.Tests;

/// <summary>Uses output recorded from Checkov 3.4.0 in M2's spike. The image smoke test runs the real one.</summary>
public class CheckovRunnerTests
{
    private const string Storage = "azurerm_storage_account.a";

    private static readonly string Canary = Failure(ScannablePlan.CanaryAddress, "CKV_AZURE_190", "Canary", "allow_nested_items_to_be_public");

    [Test]
    public async Task Keeps_allowlisted_checks_and_sets_their_severity()
    {
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(
            Canary, Failure(Storage, "CKV_AZURE_190", "Ensure that Storage blobs restrict public access", "allow_nested_items_to_be_public")));

        var violation = outcome.Violations.Single();
        await Assert.That(violation.CheckId).IsEqualTo("CKV_AZURE_190");
        await Assert.That(violation.Severity).IsEqualTo(Severity.High);
        await Assert.That(violation.Address).IsEqualTo(Storage);
    }

    // Checkov's own titles read like advice ("Ensure that...") rather than saying what's wrong.
    [Test]
    public async Task Uses_our_words_for_a_finding()
    {
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(Canary, Failure(Storage, "CKV_AZURE_190", "Ensure that Storage blobs restrict public access", null)));

        await Assert.That(outcome.Violations.Single().Title).IsEqualTo("The storage account allows public access to blobs.");
    }

    // D34: the spike's storage account failed 11 checks, most of them about related resources.
    [Test]
    public async Task Drops_checks_outside_the_allowlist()
    {
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(
            Canary, Failure(Storage, "CKV2_AZURE_38", "Soft delete", null), Failure(Storage, "CKV_AZURE_206", "Replication", "account_replication_type")));

        await Assert.That(outcome.Violations).IsEmpty();
    }

    [Test]
    public async Task Never_reports_the_canary()
    {
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(Canary));

        await Assert.That(outcome.Violations).IsEmpty();
    }

    // The spike's false clean: a plan it didn't recognize gave exit 0 and an empty summary.
    [Test]
    [Arguments("""{"passed":0,"failed":0,"skipped":0,"parsing_errors":0,"resource_count":0,"checkov_version":"3.4.0"}""")]
    [Arguments("""{"check_type":"terraform_plan","results":{"failed_checks":[]},"summary":{"failed":0}}""")]
    public async Task Never_calls_a_scan_clean_if_it_didnt_see_the_canary(string output)
    {
        var outcome = CheckovRunner.Parse(output);

        await Assert.That(((CheckovFailed)outcome).Reason).IsEqualTo(CheckovFailure.CanaryNotSeen);
    }

    [Test]
    [Arguments("not json", CheckovFailure.NoJson)]
    [Arguments("", CheckovFailure.NoJson)]
    [Arguments("""[{"results":{}}]""", CheckovFailure.MalformedResult)]
    [Arguments("""{"results":{}}""", CheckovFailure.MalformedResult)]
    [Arguments("""{"results":{"failed_checks":[{"check_id":"x"}]}}""", CheckovFailure.MalformedResult)]
    public async Task Reports_output_it_cant_read(string output, CheckovFailure expected)
    {
        await Assert.That(((CheckovFailed)CheckovRunner.Parse(output)).Reason).IsEqualTo(expected);
    }

    [Test]
    public async Task Sorts_so_the_bytes_are_stable()
    {
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(
            Failure("a.z", "CKV_AZURE_44", "t", null), Canary, Failure("a.a", "CKV_AZURE_59", "t", null), Failure("a.a", "CKV_AZURE_190", "t", null)));

        await Assert.That(outcome.Violations.Select(violation => $"{violation.Address} {violation.CheckId}")).IsEquivalentTo(
            ["a.a CKV_AZURE_190", "a.a CKV_AZURE_59", "a.z CKV_AZURE_44"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_finding_is_deterministic_points_at_the_attribute_and_needs_no_doc()
    {
        var plan = Condensed(Change(Storage, """{"allow_nested_items_to_be_public":true}"""));
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(Canary, Failure(Storage, "CKV_AZURE_190", "Public blobs", "allow_nested_items_to_be_public")));

        var finding = outcome.ToFindings(plan).Single();

        await Assert.That(finding.Deterministic).IsTrue();
        await Assert.That(finding.Agent).IsEqualTo(Reporter.Scanner);
        await Assert.That(finding.Rule).IsEqualTo("CKV_AZURE_190");
        await Assert.That(finding.Evidence.Single().Value).IsEqualTo(new PlanEvidence($"/resource_changes/{Storage}/change/after/allow_nested_items_to_be_public"));
    }

    [Test]
    [Arguments("min_tls_version", true)]
    [Arguments("min_tls_version/[0]/deep", true)]
    [Arguments("not_in_the_plan", false)]
    public async Task Falls_back_to_the_planned_values_when_the_attribute_isnt_there(string key, bool resolves)
    {
        var plan = Condensed(Change(Storage, """{"min_tls_version":"TLS1_0"}"""));
        var outcome = (CheckovRan)CheckovRunner.Parse(Output(Canary, Failure(Storage, "CKV_AZURE_44", "TLS", key)));

        var pointer = ((PlanEvidence)outcome.ToFindings(plan).Single().Evidence.Single().Value!).PlanPointer;

        await Assert.That(pointer).IsEqualTo($"/resource_changes/{Storage}/change/after" + (resolves ? "/min_tls_version" : string.Empty));
        await Assert.That(plan.Resolves(pointer)).IsTrue();
    }

    [Test]
    public async Task Scans_a_plan_with_the_canary_and_nothing_secret()
    {
        var processes = new FakeProcesses(Output(Canary));
        var plan = Redacted(Change(Storage, """{"password":"hunter2-hunter2"}"""));

        var outcome = await new CheckovRunner(processes).RunAsync(plan);

        await Assert.That(outcome).IsTypeOf<CheckovRan>();
        await Assert.That(processes.PlanSeen!).Contains(ScannablePlan.CanaryAddress);
        await Assert.That(processes.PlanSeen!).Contains("terraform_version");
        await Assert.That(processes.PlanSeen!).DoesNotContain("hunter2");
        await Assert.That(processes.Arguments!).Contains("terraform_plan");
    }

    [Test]
    public async Task A_plan_that_only_deletes_has_nothing_to_scan()
    {
        var processes = new FakeProcesses(string.Empty);
        const string Delete = """
            {"address":"a.b","type":"a","mode":"managed","change":{"actions":["delete"],"before":{},"after":null,
            "after_unknown":{},"before_sensitive":{},"after_sensitive":false} }
            """;

        var outcome = await new CheckovRunner(processes).RunAsync(Redacted(Delete));

        await Assert.That(((CheckovRan)outcome).Violations).IsEmpty();
        await Assert.That(processes.PlanSeen).IsNull();
    }

    [Test]
    public async Task Reports_a_scan_that_didnt_finish()
    {
        var outcome = await new CheckovRunner(new FakeProcesses(null)).RunAsync(Redacted(Change(Storage, "{}")));

        await Assert.That(((CheckovFailed)outcome).Reason).IsEqualTo(CheckovFailure.CouldNotRun);
    }

    private static string Output(params string[] failures) =>
        $$"""{"check_type":"terraform_plan","results":{"failed_checks":[{{string.Join(',', failures)}}]},"summary":{"failed":{{failures.Length}} } }""";

    private static string Failure(string address, string checkId, string name, string? key)
    {
        var keys = key is null ? "[]" : $"[\"{key}\"]";
        return $$"""
            {"check_id":"{{checkId}}","check_name":"{{name}}","resource":"{{address}}","resource_address":"{{address}}","check_result":{"result":"FAILED","evaluated_keys":{{keys}} } }
            """;
    }

    private static string Change(string address, string after) => $$"""
        {"address":"{{address}}","type":"azurerm_storage_account","mode":"managed","change":{"actions":["create"],"before":null,"after":{{after}},"after_unknown":{},"before_sensitive":false,"after_sensitive":{} } }
        """;

    private static RedactedPlan Redacted(params string[] changes) =>
        ((Redacted)new PlanRedactor().Redact($$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', changes)}}]}""")).Plan;

    private static CondensedPlan Condensed(params string[] changes) => CondensedPlan.From(Redacted(changes));

    private sealed class FakeProcesses(string? output) : IProcessRunner
    {
        public string? PlanSeen { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public async Task<ProcessResult?> RunAsync(
            string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            PlanSeen = await File.ReadAllTextAsync(arguments[1], cancellationToken);
            return output is null ? null : new ProcessResult(1, output);
        }
    }
}
