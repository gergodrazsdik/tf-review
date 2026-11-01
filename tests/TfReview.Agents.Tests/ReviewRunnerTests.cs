using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents.Tests;

public class ReviewRunnerTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StandardsCommit = "0123456789abcdef0123456789abcdef01234567";

    private const string Config = $"""
        version: 1
        standards:
          repository: owner/standards
          commit: {StandardsCommit}
        """;

    private const string Plan = """
        {"format_version":"1.2","resource_changes":[{"address":"a.b","type":"a","mode":"managed","change":{
        "actions":["create"],"before":null,"after":{"name":"x","tags":null},"after_unknown":{},
        "before_sensitive":false,"after_sensitive":{}}}]}
        """;

    private const string OneViolation = """
        [{"namespace":"adr002","failures":[{"msg":"ADR-002: a.b: missing","metadata":{"details":
        {"address":"a.b","doc_id":"ADR-002","severity":"medium","attribute":"tags"}}}]}]
        """;

    private const string NoViolations = """[{"namespace":"adr002","successes":1}]""";

    [Test]
    public async Task Reports_policy_findings()
    {
        var result = await Run(Plan, Config, OneViolation);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.Findings);
        await Assert.That(result.Commit).IsEqualTo(Commit);
        await Assert.That(result.StandardsCommit).IsEqualTo(StandardsCommit);
        await Assert.That(result.Findings.Single().Rule).IsEqualTo("ADR-002");
        await Assert.That(result.Findings.Single().Evidence[0].Value)
            .IsEqualTo(new PlanEvidence("/resource_changes/a.b/change/after/tags"));
    }

    [Test]
    public async Task Says_the_agent_didnt_run_even_with_no_findings()
    {
        var result = await Run(Plan, Config, NoViolations);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Conventions, NotReviewedReason.Unavailable));
    }

    [Test]
    public async Task Sorts_the_most_severe_finding_first()
    {
        const string Output = """
            [{"namespace":"x","failures":[
            {"msg":"low","metadata":{"details":{"address":"a.a","doc_id":"ADR-001","severity":"low"}}},
            {"msg":"high","metadata":{"details":{"address":"a.z","doc_id":"POL-001","severity":"high"}}}]}]
            """;

        var result = await Run(Plan, Config, Output);

        await Assert.That(result.Findings[0].Severity).IsEqualTo(Severity.High);
    }

    // The artifact comes from a job that ran PR code, so it can't be taken on trust.
    [Test]
    [Arguments("""{"refused":true}""")]
    [Arguments("not json")]
    [Arguments("""{"format_version":"1.2","resource_changes":[{"address":"a.b"}]}""")]
    public async Task Refuses_a_plan_that_doesnt_pass_redaction(string plan)
    {
        var result = await Run(plan, Config, NoViolations);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NotReviewed);
        await Assert.That(result.NotReviewed.Single().Reason).IsEqualTo(NotReviewedReason.RedactionRefused);
    }

    [Test]
    public async Task Redacts_again_what_a_tampered_artifact_left_in()
    {
        const string Tampered = """
            {"format_version":"1.2","resource_changes":[{"address":"a.b","type":"a","mode":"managed","change":{
            "actions":["create"],"before":null,"after":{"password":"left-in-by-tampering"},"after_unknown":{},
            "before_sensitive":false,"after_sensitive":{}}}]}
            """;
        var conftest = new FakeProcesses(NoViolations);

        await Run(Tampered, Config, conftest);

        await Assert.That(conftest.PlanSeen).DoesNotContain("left-in-by-tampering");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("version: 2")]
    public async Task Reports_an_invalid_config(string? config)
    {
        var result = await Run(Plan, config, NoViolations);

        await Assert.That(result.NotReviewed.Single().Reason).IsEqualTo(NotReviewedReason.InvalidConfig);
    }

    [Test]
    public async Task Reports_standards_it_couldnt_fetch()
    {
        var runner = new ReviewRunner(new FailingSource(), new ConftestRunner(new FakeProcesses(NoViolations)));

        var result = await runner.RunAsync(Plan, Config, Commit);

        await Assert.That(result.NotReviewed.Single().Reason).IsEqualTo(NotReviewedReason.StandardsUnavailable);
    }

    // The false clean: conftest exits 0 and checked nothing.
    [Test]
    [Arguments("""[{"namespace":"main","successes":0}]""")]
    [Arguments("Error: no policies found")]
    public async Task Never_calls_a_failed_policy_run_clean(string output)
    {
        var result = await Run(Plan, Config, output);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NotReviewed);
        await Assert.That(result.NotReviewed.Single().Reason).IsEqualTo(NotReviewedReason.ScannerFailed);
    }

    [Test]
    public async Task Fetches_the_commit_the_config_names()
    {
        var source = new RecordingSource();
        await new ReviewRunner(source, new ConftestRunner(new FakeProcesses(NoViolations))).RunAsync(Plan, Config, Commit);

        await Assert.That(source.Requested!.Repository).IsEqualTo("owner/standards");
        await Assert.That(source.Requested!.Commit).IsEqualTo(StandardsCommit);
    }

    [Test]
    public async Task Cleans_up_the_standards_it_fetched()
    {
        var source = new RecordingSource();
        await new ReviewRunner(source, new ConftestRunner(new FakeProcesses(NoViolations))).RunAsync(Plan, Config, Commit);

        await Assert.That(Directory.Exists(source.Directory)).IsFalse();
    }

    private static Task<ReviewResult> Run(string plan, string? config, string conftestOutput) =>
        Run(plan, config, new FakeProcesses(conftestOutput));

    private static Task<ReviewResult> Run(string plan, string? config, FakeProcesses processes) =>
        new ReviewRunner(new RecordingSource(), new ConftestRunner(processes)).RunAsync(plan, config, Commit);

    private sealed class RecordingSource : IStandardsSource
    {
        public TfReviewConfig? Requested { get; private set; }

        public string? Directory { get; private set; }

        public Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default)
        {
            Requested = config;
            Directory = targetDirectory;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSource : IStandardsSource
    {
        public Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default) =>
            throw new StandardsFetchException("Downloading the standards failed.");
    }

    private sealed class FakeProcesses(string output) : IProcessRunner
    {
        public string? PlanSeen { get; private set; }

        public async Task<ProcessResult?> RunAsync(
            string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            PlanSeen = await File.ReadAllTextAsync(arguments[^1], cancellationToken);
            return new ProcessResult(1, output);
        }
    }
}
