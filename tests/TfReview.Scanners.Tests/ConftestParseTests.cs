using TfReview.Core;

namespace TfReview.Scanners.Tests;

public class ConftestParseTests
{
    // Recorded from conftest 0.71.1 without --all-namespaces: exit 0, and nothing was checked.
    private const string OnlyDefaultNamespace = """
        [{"filename":"plan.json","namespace":"main","successes":0}]
        """;

    private const string Clean = """
        [{"filename":"p","namespace":"pol001","successes":1},{"filename":"p","namespace":"adr001","successes":1}]
        """;

    private const string TwoTagsMissing = """
        [{"filename":"p","namespace":"adr002","successes":0,"failures":[
          {"msg":"ADR-002: a.b: required tag \"owner\" is missing or empty","metadata":{"details":{"address":"a.b","doc_id":"ADR-002","severity":"medium"},"query":"data.adr002.deny"}},
          {"msg":"ADR-002: a.b: required tag \"environment\" is missing or empty","metadata":{"details":{"address":"a.b","doc_id":"ADR-002","severity":"medium"},"query":"data.adr002.deny"}}]},
         {"filename":"p","namespace":"adr001","successes":0,"failures":[
          {"msg":"ADR-001: a.a: name bad","metadata":{"details":{"address":"a.a","doc_id":"ADR-001","severity":"low"},"query":"data.adr001.deny"}}]}]
        """;

    [Test]
    public async Task A_run_that_only_read_the_default_namespace_is_not_clean()
    {
        await Assert.That(ConftestRunner.Parse(OnlyDefaultNamespace)).IsEqualTo(new ConftestFailed(ConftestFailure.NoPolicyRan));
    }

    [Test]
    public async Task No_results_at_all_is_not_clean()
    {
        await Assert.That(ConftestRunner.Parse("[]")).IsEqualTo(new ConftestFailed(ConftestFailure.NoPolicyRan));
    }

    // conftest exits 1 for this too, so the exit code can't tell it apart from a policy failure.
    [Test]
    [Arguments("Error: running test: load: loading policies: no policies found")]
    [Arguments("")]
    [Arguments("""{"not":"an array"}""")]
    public async Task Output_that_isnt_a_result_list_is_not_clean(string output)
    {
        await Assert.That(ConftestRunner.Parse(output)).IsEqualTo(new ConftestFailed(ConftestFailure.NoJson));
    }

    [Test]
    public async Task Policies_that_passed_are_clean()
    {
        var outcome = (ConftestRan)ConftestRunner.Parse(Clean);

        await Assert.That(outcome.Violations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Reads_and_sorts_violations()
    {
        var outcome = (ConftestRan)ConftestRunner.Parse(TwoTagsMissing);

        await Assert.That(outcome.Violations.Select(violation => violation.Address).ToArray())
            .IsEquivalentTo(["a.a", "a.b", "a.b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(outcome.Violations[0]).IsEqualTo(new ConftestViolation("ADR-001", "a.a", Severity.Low, "ADR-001: a.a: name bad"));
    }

    [Test]
    public async Task Merges_violations_into_one_finding_per_resource_and_rule()
    {
        var findings = ((ConftestRan)ConftestRunner.Parse(TwoTagsMissing)).ToFindings(Condensed());

        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings[1].Rule).IsEqualTo("ADR-002");
        await Assert.That(findings[1].Title).Contains("\"owner\"");
        await Assert.That(findings[1].Title).Contains("\"environment\"");
    }

    [Test]
    public async Task Findings_say_a_scanner_found_them_and_carry_both_kinds_of_evidence()
    {
        var finding = ((ConftestRan)ConftestRunner.Parse(TwoTagsMissing)).ToFindings(Condensed())[0];

        await Assert.That(finding.Agent).IsEqualTo(Reporter.Scanner);
        await Assert.That(finding.Deterministic).IsTrue();
        await Assert.That(finding.Evidence[0].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after"));
        await Assert.That(finding.Evidence[1].Value).IsEqualTo(new DocEvidence("ADR-001"));
    }

    [Test]
    [Arguments("""[{"namespace":"x","failures":[{"msg":"m"}]}]""")]
    [Arguments("""[{"namespace":"x","failures":[{"msg":"m","metadata":{"details":{"address":"a.b","doc_id":"nope","severity":"low"}}}]}]""")]
    [Arguments("""[{"namespace":"x","failures":[{"msg":"m","metadata":{"details":{"address":"a.b","doc_id":"ADR-001","severity":"urgent"}}}]}]""")]
    [Arguments("""[{"namespace":"x","failures":[{"msg":"m","metadata":{"details":{"address":"","doc_id":"ADR-001","severity":"low"}}}]}]""")]
    [Arguments("""[{"failures":[]}]""")]
    public async Task A_failure_a_policy_didnt_describe_properly_is_not_clean(string output)
    {
        await Assert.That(ConftestRunner.Parse(output)).IsEqualTo(new ConftestFailed(ConftestFailure.MalformedResult));
    }

    [Test]
    public async Task Points_at_the_attribute_that_failed()
    {
        var findings = Findings("name");

        await Assert.That(findings[0].Evidence[0].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after/name"));
    }

    // POL-001 fires on a missing attribute, which a pointer can't reach. Without this fallback, the
    // evidence check would drop a real finding.
    [Test]
    public async Task Falls_back_to_the_planned_values_when_the_attribute_isnt_there()
    {
        var findings = Findings("min_tls_version");

        await Assert.That(findings[0].Evidence[0].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after"));
    }

    [Test]
    public async Task Falls_back_when_a_policy_doesnt_report_an_attribute()
    {
        var finding = ((ConftestRan)ConftestRunner.Parse(TwoTagsMissing)).ToFindings(Condensed())[0];

        await Assert.That(finding.Evidence[0].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after"));
    }

    [Test]
    public async Task Every_pointer_resolves_whatever_the_policy_reports()
    {
        var plan = Condensed();

        foreach (var attribute in new[] { "name", "min_tls_version" })
        {
            var pointer = (PlanEvidence)Findings(attribute)[0].Evidence[0].Value!;
            await Assert.That(plan.Resolves(pointer.PlanPointer)).IsTrue();
        }
    }

    [Test]
    public async Task Keeps_a_pointer_per_failed_attribute()
    {
        var outcome = (ConftestRan)ConftestRunner.Parse("""
            [{"namespace":"pol001","failures":[
              {"msg":"one","metadata":{"details":{"address":"a.a","doc_id":"POL-001","severity":"high","attribute":"tags"}}},
              {"msg":"two","metadata":{"details":{"address":"a.a","doc_id":"POL-001","severity":"high","attribute":"name"}}}]}]
            """);

        var evidence = outcome.ToFindings(Condensed()).Single().Evidence;

        await Assert.That(evidence.Count).IsEqualTo(3);
        await Assert.That(evidence[0].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after/name"));
        await Assert.That(evidence[1].Value).IsEqualTo(new PlanEvidence("/resource_changes/a.a/change/after/tags"));
    }

    [Test]
    public async Task Rejects_an_attribute_that_could_change_the_pointer()
    {
        const string Output = """
            [{"namespace":"x","failures":[{"msg":"m","metadata":{"details":
            {"address":"a.a","doc_id":"ADR-001","severity":"low","attribute":"../../other"}}}]}]
            """;

        await Assert.That(ConftestRunner.Parse(Output)).IsEqualTo(new ConftestFailed(ConftestFailure.MalformedResult));
    }

    private static IReadOnlyList<Finding> Findings(string attribute)
    {
        var outcome = (ConftestRan)ConftestRunner.Parse($$$"""
            [{"namespace":"adr001","failures":[{"msg":"m","metadata":{"details":
            {"address":"a.a","doc_id":"ADR-001","severity":"low","attribute":"{{{attribute}}}" } } }]}]
            """);
        return outcome.ToFindings(Condensed());
    }

    private static CondensedPlan Condensed()
    {
        const string Plan = """
            {"format_version":"1.2","resource_changes":[{"address":"a.a","type":"a","mode":"managed","change":{
            "actions":["create"],"before":null,"after":{"name":"x","tags":null},"after_unknown":{},
            "before_sensitive":false,"after_sensitive":{}}}]}
            """;
        return CondensedPlan.From(((Redacted)new PlanRedactor().Redact(Plan)).Plan);
    }
}
