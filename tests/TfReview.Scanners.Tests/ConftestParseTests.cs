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
        var findings = ((ConftestRan)ConftestRunner.Parse(TwoTagsMissing)).ToFindings();

        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings[1].Rule).IsEqualTo("ADR-002");
        await Assert.That(findings[1].Title).Contains("\"owner\"");
        await Assert.That(findings[1].Title).Contains("\"environment\"");
    }

    [Test]
    public async Task Findings_say_a_scanner_found_them_and_carry_both_kinds_of_evidence()
    {
        var finding = ((ConftestRan)ConftestRunner.Parse(TwoTagsMissing)).ToFindings()[0];

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
}
