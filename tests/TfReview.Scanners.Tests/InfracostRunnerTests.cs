using TfReview.Core;

namespace TfReview.Scanners.Tests;

/// <summary>Uses output recorded from Infracost 2.18.3 in M2's spike, so these need no token.</summary>
public class InfracostRunnerTests
{
    private const string Vm = "azurerm_linux_virtual_machine.main";

    private static readonly string Update = Change(Vm, "azurerm_linux_virtual_machine", "update", """{"size":"Standard_B2s"}""", """{"size":"Standard_D4s_v5"}""");

    [Test]
    public async Task Works_out_the_monthly_change_from_two_scans()
    {
        var outcome = await Run(Plan(Update), Scan(Vm, 30.368m, 1.536m), Scan(Vm, 140.16m, 1.536m));

        var cost = ((InfracostRan)outcome).Costs.Single();
        await Assert.That(cost.Address).IsEqualTo(Vm);
        await Assert.That(cost.Before).IsEqualTo(31.904m);
        await Assert.That(cost.After).IsEqualTo(141.696m);
        await Assert.That(cost.Delta).IsEqualTo(109.792m);
        await Assert.That(cost.Priced).IsTrue();
        await Assert.That(((InfracostRan)outcome).TotalDelta).IsEqualTo(109.792m);
    }

    [Test]
    public async Task A_create_is_only_scanned_once_and_costs_nothing_before()
    {
        var create = Change("a.new", "a", "create", "null", "{}");
        var processes = new FakeProcesses(after: Scan("a.new", 10m, 0m));

        var outcome = await new InfracostRunner(processes).RunAsync(Plan(create));

        var cost = ((InfracostRan)outcome).Costs.Single();
        await Assert.That(cost.Before).IsEqualTo(0m);
        await Assert.That(cost.Delta).IsEqualTo(10m);
        await Assert.That(processes.Scans).IsEqualTo(1);
    }

    [Test]
    public async Task A_delete_saves_what_it_cost()
    {
        var delete = Change("a.old", "a", "delete", "{}", "null");

        var outcome = await new InfracostRunner(new FakeProcesses(before: Scan("a.old", 10m, 0m))).RunAsync(Plan(delete));

        await Assert.That(((InfracostRan)outcome).TotalDelta).IsEqualTo(-10m);
    }

    [Test]
    public async Task A_plan_with_nothing_to_price_doesnt_use_a_scan()
    {
        var processes = new FakeProcesses();

        var outcome = await new InfracostRunner(processes).RunAsync(Plan());

        await Assert.That(((InfracostRan)outcome).Costs).IsEmpty();
        await Assert.That(processes.Scans).IsEqualTo(0);
    }

    // An unknown price isn't a free resource, and a total shouldn't count it as one.
    [Test]
    public async Task A_type_infracost_doesnt_support_is_reported_as_unpriced()
    {
        const string Unsupported = """
            {"projects":[{"resources":[{"name":"azurerm_linux_virtual_machine.main","type":"x","is_supported":false}]}]}
            """;

        var outcome = await Run(Plan(Update), Unsupported, Unsupported);

        var ran = (InfracostRan)outcome;
        await Assert.That(ran.Costs.Single().Priced).IsFalse();
        await Assert.That(ran.TotalDelta).IsEqualTo(0m);
    }

    [Test]
    public async Task A_resource_missing_from_a_scan_is_unpriced()
    {
        var outcome = await Run(Plan(Update), Scan("a.other", 1m, 0m), Scan("a.other", 1m, 0m));

        await Assert.That(((InfracostRan)outcome).Costs.Single(cost => cost.Address == Vm).Priced).IsFalse();
    }

    [Test]
    public async Task A_free_resource_is_priced_at_zero()
    {
        const string Free = """
            {"projects":[{"resources":[{"name":"azurerm_linux_virtual_machine.main","type":"x","is_supported":true,"is_free":true}]}]}
            """;

        var cost = ((InfracostRan)await Run(Plan(Update), Free, Free)).Costs.Single();

        await Assert.That(cost.Priced).IsTrue();
        await Assert.That(cost.After).IsEqualTo(0m);
    }

    // The spike's false clean: a plan it couldn't read gave exit 0, no resources and a total of $0.
    [Test]
    public async Task Never_calls_an_empty_scan_clean()
    {
        const string Empty = """
            {"currency":"USD","summary":{"projects":1,"resources":0,"total_monthly_cost":"0"},
            "projects":[{"project_name":"x","finops_results":[],"resources":[]}]}
            """;

        var outcome = await Run(Plan(Update), Empty, Empty);

        await Assert.That(((InfracostFailed)outcome).Reason).IsEqualTo(InfracostFailure.NoResources);
    }

    [Test]
    [Arguments("not json", InfracostFailure.NoJson)]
    [Arguments("", InfracostFailure.NoJson)]
    [Arguments("""{"currency":"USD"}""", InfracostFailure.MalformedResult)]
    [Arguments("""[1,2]""", InfracostFailure.MalformedResult)]
    [Arguments("""{"projects":[{"resources":[{"name":"a.b","is_supported":true,"cost_components":[{"total_monthly_cost":"lots"}]}]}]}""", InfracostFailure.MalformedResult)]
    [Arguments("""{"projects":[{"resources":[{"name":"a.b"}]}]}""", InfracostFailure.MalformedResult)]
    public async Task Reports_output_it_cant_read(string output, InfracostFailure expected)
    {
        var outcome = await Run(Plan(Update), output, output);

        await Assert.That(((InfracostFailed)outcome).Reason).IsEqualTo(expected);
    }

    // With no token a scan waits forever, which ends as a timeout here.
    [Test]
    public async Task Reports_a_scan_that_didnt_finish()
    {
        var outcome = await new InfracostRunner(new FakeProcesses(fail: true)).RunAsync(Plan(Update));

        await Assert.That(((InfracostFailed)outcome).Reason).IsEqualTo(InfracostFailure.CouldNotRun);
    }

    [Test]
    public async Task Scans_with_no_shell_and_cleans_up()
    {
        var processes = new FakeProcesses(Scan(Vm, 1m, 0m), Scan(Vm, 2m, 0m));

        await new InfracostRunner(processes).RunAsync(Plan(Update));

        await Assert.That(processes.Arguments!).Contains("--json");
        await Assert.That(processes.Arguments!).Contains("USD");
        await Assert.That(File.Exists(processes.Path!)).IsFalse();
    }

    [Test]
    public async Task Scans_a_plan_infracost_can_recognize()
    {
        var processes = new FakeProcesses(Scan(Vm, 1m, 0m), Scan(Vm, 2m, 0m));

        await new InfracostRunner(processes).RunAsync(Plan(Update));

        await Assert.That(processes.LastPlan!).Contains("\"format_version\"");
        await Assert.That(processes.LastPlan!).Contains("\"planned_values\"");
        await Assert.That(processes.LastPlan!).Contains("Standard_D4s_v5");
    }

    private static Task<InfracostOutcome> Run(RedactedPlan plan, string before, string after) =>
        new InfracostRunner(new FakeProcesses(before, after)).RunAsync(plan);

    private static RedactedPlan Plan(params string[] changes) =>
        ((Redacted)new PlanRedactor().Redact(
            $$"""{"format_version":"1.2","resource_changes":[{{string.Join(',', changes)}}]}""")).Plan;

    private static string Change(string address, string type, string action, string before, string after) => $$"""
        {"address":"{{address}}","type":"{{type}}","mode":"managed","name":"main","change":{"actions":["{{action}}"],"before":{{before}},"after":{{after}},"after_unknown":{},"before_sensitive":{{(before == "null" ? "false" : "{}")}},"after_sensitive":{{(after == "null" ? "false" : "{}")}} } }
        """;

    // The shape recorded in the spike, including the tagging policy the runner has to ignore.
    private static string Scan(string address, decimal instance, decimal disk) => $$"""
        {"currency":"USD","summary":{"projects":1,"resources":1,"costed_resources":1},
        "projects":[{"project_name":"p","finops_results":[],
        "tagging_results":[{"policy_name":"FinOps tags","failing_resources":[{"address":"{{address}}"}]}],
        "resources":[{"name":"{{address}}","type":"t","is_supported":true,"is_free":false,
        "cost_components":[{"name":"Instance usage","price":"0.1","quantity":"730","total_monthly_cost":"{{instance}}"}],
        "subresources":[{"name":"os_disk","is_supported":false,"cost_components":[
        {"name":"Storage","total_monthly_cost":"{{disk}}"},{"name":"Disk operations","total_monthly_cost":"0","usage_based":true}]}]}]}]}
        """;

    private sealed class FakeProcesses(string? before = null, string? after = null, bool fail = false) : IProcessRunner
    {
        public int Scans { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public string? Path { get; private set; }

        public string? LastPlan { get; private set; }

        public async Task<ProcessResult?> RunAsync(
            string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Scans++;
            Arguments = arguments;
            Path = arguments[1];
            LastPlan = await File.ReadAllTextAsync(Path, cancellationToken);
            if (fail)
            {
                return null;
            }

            return new ProcessResult(0, Path.EndsWith("before.json", StringComparison.Ordinal) ? before ?? "{}" : after ?? "{}");
        }
    }
}
