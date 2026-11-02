using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents.Tests;

public class ReviewRunnerCostTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Vm = "azurerm_linux_virtual_machine.main";
    private const string NoViolations = """[{"namespace":"adr002","successes":1}]""";

    private const string WithCost = """
        version: 1
        standards:
          repository: owner/standards
          commit: 0123456789abcdef0123456789abcdef01234567
        agents: [cost]
        """;

    private const string WithoutCost = """
        version: 1
        standards:
          repository: owner/standards
          commit: 0123456789abcdef0123456789abcdef01234567
        agents: []
        """;

    private static readonly string Plan = TestPlans.Json(
        TestPlans.Update(Vm, "azurerm_linux_virtual_machine", """{"size":"small"}""", """{"size":"large"}"""));

    [Test]
    public async Task Reports_the_plans_monthly_cost()
    {
        var result = await Run(WithCost, new FakeScans(Scan(31.904m), Scan(141.696m)));

        await Assert.That(result.Cost!.BeforeUsd).IsEqualTo(31.904m);
        await Assert.That(result.Cost.AfterUsd).IsEqualTo(141.696m);
        await Assert.That(result.Cost.Resources.Single().ResourceAddress).IsEqualTo(Vm);
    }

    // The agents' prices come from the scan, so without a model the figures still reach the comment.
    [Test]
    public async Task Without_a_model_an_increase_is_still_shown_and_the_agent_reported_as_not_reviewed()
    {
        var result = await Run(WithCost, new FakeScans(Scan(1m), Scan(2m)));

        await Assert.That(result.Cost!.AfterUsd).IsEqualTo(2m);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Cost, NotReviewedReason.Unavailable));
    }

    // Nothing to judge, so there's nothing to say wasn't reviewed.
    [Test]
    public async Task No_increase_needs_no_agent()
    {
        var result = await Run(WithCost, new FakeScans(Scan(2m), Scan(1m)));

        await Assert.That(result.NotReviewed).IsEmpty();
    }

    [Test]
    public async Task A_repo_that_didnt_opt_in_isnt_priced_or_told_it_wasnt()
    {
        var scans = new FakeScans(Scan(1m), Scan(2m));

        var result = await Run(WithoutCost, scans);

        await Assert.That(scans.Calls).IsEqualTo(0);
        await Assert.That(result.Cost).IsNull();
        await Assert.That(result.NotReviewed).IsEmpty();
    }

    [Test]
    public async Task No_token_is_reported_as_not_reviewed()
    {
        var result = await new ReviewRunner(new EmptySource(), new ConftestRunner(new FakeProcesses(NoViolations))).RunAsync(Plan, WithCost, Commit);

        await Assert.That(result.Cost).IsNull();
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Cost, NotReviewedReason.Unavailable));
    }

    // A failed scan isn't a plan that costs nothing, and it doesn't undo the rest of the review.
    [Test]
    public async Task A_failed_scan_is_reported_and_leaves_the_rest_standing()
    {
        const string Violation = """
            [{"namespace":"adr002","failures":[{"msg":"m","metadata":{"details":
            {"address":"azurerm_linux_virtual_machine.main","doc_id":"ADR-002","severity":"medium"}}}]}]
            """;
        var runner = new ReviewRunner(new EmptySource(), new ConftestRunner(new FakeProcesses(Violation)), infracost: new InfracostRunner(new FakeScans("not json", "not json")));

        var result = await runner.RunAsync(Plan, WithCost, Commit);

        await Assert.That(result.Cost).IsNull();
        await Assert.That(result.Findings.Count).IsEqualTo(1);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Cost, NotReviewedReason.ScannerFailed));
    }

    // The comment can only say a scanner failed, so the log has to say how, and it holds nothing from the plan.
    [Test]
    public async Task A_failed_scan_logs_why()
    {
        var log = new List<string>();
        var runner = new ReviewRunner(new EmptySource(), new ConftestRunner(new FakeProcesses(NoViolations)), log: log.Add, infracost: new InfracostRunner(new FakeScans("not json", "not json")));

        await runner.RunAsync(Plan, WithCost, Commit);

        await Assert.That(log.Single(line => line.StartsWith("infracost:", StringComparison.Ordinal))).Matches("^infracost: [A-Za-z]+\\.$");
    }

    [Test]
    public async Task An_unpriced_resource_isnt_counted_as_free()
    {
        const string Unsupported = """{"projects":[{"resources":[{"name":"azurerm_linux_virtual_machine.main","is_supported":false}]}]}""";

        var result = await Run(WithCost, new FakeScans(Unsupported, Unsupported));

        await Assert.That(result.Cost!.Resources.Single().Priced).IsFalse();
        await Assert.That(result.Cost.AfterUsd).IsEqualTo(0m);
    }

    [Test]
    public async Task The_cost_survives_the_trip_to_the_publish_job()
    {
        var result = await Run(WithCost, new FakeScans(Scan(1.5m), Scan(2.5m)));

        var parsed = ReviewResult.Parse(System.Text.Json.JsonSerializer.Serialize(result));

        await Assert.That(parsed.Cost!.AfterUsd).IsEqualTo(2.5m);
        await Assert.That(parsed.Cost.Resources.Single().Priced).IsTrue();
    }

    [Test]
    public async Task Reports_what_checkov_found_with_the_policies()
    {
        const string Output = """
            {"results":{"failed_checks":[
            {"check_id":"CKV_AZURE_190","check_name":"Canary","resource":"azurerm_storage_account.tfreview_canary"},
            {"check_id":"CKV_AZURE_190","check_name":"Public blobs","resource":"azurerm_linux_virtual_machine.main","check_result":{"evaluated_keys":["size"]}}
            ]}}
            """;

        var result = await RunSecurity(new FakeProcesses(Output));

        var finding = result.Findings.Single();
        await Assert.That(finding.Rule).IsEqualTo("CKV_AZURE_190");
        await Assert.That(finding.Deterministic).IsTrue();
    }

    // The false clean: it read nothing and exited 0.
    [Test]
    public async Task A_checkov_scan_that_saw_nothing_is_reported_and_not_called_clean()
    {
        var result = await RunSecurity(new FakeProcesses("""{"passed":0,"failed":0,"resource_count":0}"""));

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Security, NotReviewedReason.ScannerFailed));
    }

    private static Task<ReviewResult> RunSecurity(FakeProcesses checkov)
    {
        var config = WithoutCost.Replace("agents: []", "agents: [security]", StringComparison.Ordinal);
        return new ReviewRunner(new EmptySource(), new ConftestRunner(new FakeProcesses(NoViolations)), checkov: new CheckovRunner(checkov)).RunAsync(Plan, config, Commit);
    }

    private static Task<ReviewResult> Run(string config, FakeScans scans) =>
        new ReviewRunner(new EmptySource(), new ConftestRunner(new FakeProcesses(NoViolations)), infracost: new InfracostRunner(scans)).RunAsync(Plan, config, Commit);

    private static string Scan(decimal cost) => $$"""
        {"projects":[{"resources":[{"name":"{{Vm}}","is_supported":true,"cost_components":[{"total_monthly_cost":"{{cost}}"}]}]}]}
        """;

    private sealed class EmptySource : IStandardsSource
    {
        public Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeScans(string before, string after) : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult?> RunAsync(
            string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            var output = arguments[1].EndsWith("before.json", StringComparison.Ordinal) ? before : after;
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, output));
        }
    }
}
