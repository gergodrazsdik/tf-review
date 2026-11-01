using System.ClientModel;
using System.Text.Json;
using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents.Tests;

/// <summary>The whole review with a fake model and fake conftest, and the real corpus reader, agent, evidence check and arbiter.</summary>
public class ReviewRunnerAgentTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StandardsCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string Vnet = "azurerm_virtual_network.main";
    private const string Subnet = "azurerm_subnet.main";

    private const string Config = $"""
        version: 1
        standards:
          repository: owner/standards
          commit: {StandardsCommit}
        """;

    private const string NoViolations = """[{"namespace":"adr002","successes":1}]""";

    private const string TagViolation = """
        [{"namespace":"adr002","failures":[{"msg":"ADR-002: missing tags","metadata":{"details":
        {"address":"azurerm_virtual_network.main","doc_id":"ADR-002","severity":"medium","attribute":"tags"}}}]}]
        """;

    private const string Tagging = """
        ---
        id: ADR-002
        type: adr
        status: accepted
        applies_to: ["*"]
        topics: [tagging]
        severity: medium
        ---

        environment is dev, test or prod.
        """;

    private const string Empty = """{"findings":[]}""";

    private static readonly string VnetPlan = TestPlans.Json(
        TestPlans.Create(Vnet, "azurerm_virtual_network", """{"name":"vnet-a-prod","tags":{"environment":"production"}}"""));

    private static readonly string SubnetPlan = TestPlans.Json(TestPlans.Create(Subnet, "azurerm_subnet", """{"name":"snet-a-prod"}"""));

    private static string Answer(string address, string doc = "ADR-002", string attribute = "tags") =>
        $$"""{"findings":[{"resource_address":"{{address}}","doc_id":"{{doc}}","attribute":"{{attribute}}","title":"Model title","suggestion":"Model suggestion"}]}""";

    // C9: the policies pass, and only the model can see the value breaks the standard.
    [Test]
    public async Task Reports_what_only_the_model_found()
    {
        var chat = new FakeChat(FakeChat.Answer(Answer(Vnet), 100, 10));

        var result = await Run(VnetPlan, chat, NoViolations);

        var finding = result.Findings.Single();
        await Assert.That(result.Verdict).IsEqualTo(Verdict.Findings);
        await Assert.That(result.NotReviewed).IsEmpty();
        await Assert.That(finding.Agent).IsEqualTo(Reporter.Conventions);
        await Assert.That(finding.Deterministic).IsFalse();
        await Assert.That(finding.Severity).IsEqualTo(Severity.Medium);
    }

    // C5: both find it, so it's one row, still deterministic, with the policy's words and the model's fix.
    [Test]
    public async Task Merges_what_the_model_and_a_policy_both_found()
    {
        var chat = new FakeChat(FakeChat.Answer(Answer(Vnet)));

        var result = await Run(VnetPlan, chat, TagViolation);

        var finding = result.Findings.Single();
        await Assert.That(finding.Deterministic).IsTrue();
        await Assert.That(finding.Agent).IsEqualTo(Reporter.Conventions);
        await Assert.That(finding.Title).IsEqualTo("ADR-002: missing tags");
        await Assert.That(finding.Suggestion).IsEqualTo("Model suggestion");
    }

    // C10: a subnet has no tags attribute, so this is dropped however sure the model sounds.
    [Test]
    public async Task Drops_a_finding_about_an_attribute_the_resource_doesnt_have()
    {
        var chat = new FakeChat(FakeChat.Answer(Answer(Subnet)));

        var result = await Run(SubnetPlan, chat, NoViolations);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(result.NotReviewed).IsEmpty();
        await Assert.That(result.EvidenceDrops[EvidenceChecker.PointerUnresolved]).IsEqualTo(1);
    }

    [Test]
    public async Task A_model_failure_keeps_the_policy_findings_and_says_what_wasnt_reviewed()
    {
        var chat = new FakeChat(new ClientResultException("Too many requests."));

        var result = await Run(VnetPlan, chat, TagViolation);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.Findings);
        await Assert.That(result.Findings.Single().Agent).IsEqualTo(Reporter.Scanner);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Conventions, NotReviewedReason.Unavailable));
    }

    [Test]
    public async Task A_broken_doc_stops_the_agent_but_keeps_the_policy_findings()
    {
        var chat = new FakeChat();

        var result = await Run(VnetPlan, chat, TagViolation, docs: "---\nid: broken\n---\n");

        await Assert.That(chat.Requests).IsEmpty();
        await Assert.That(result.Findings.Count).IsEqualTo(1);
        await Assert.That(result.NotReviewed.Single()).IsEqualTo(new NotReviewed(ReviewScope.Conventions, NotReviewedReason.StandardsUnavailable));
    }

    [Test]
    public async Task A_repo_that_turned_the_agent_off_isnt_told_it_didnt_run()
    {
        var chat = new FakeChat();

        var result = await Run(VnetPlan, chat, NoViolations, config: Config + "\nagents: []\n");

        await Assert.That(chat.Requests).IsEmpty();
        await Assert.That(result.NotReviewed).IsEmpty();
    }

    [Test]
    public async Task A_plan_with_no_changes_costs_nothing_and_counts_as_reviewed()
    {
        var chat = new FakeChat();

        var result = await Run(TestPlans.Json(), chat, NoViolations);

        await Assert.That(chat.Requests).IsEmpty();
        await Assert.That(result.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(result.NotReviewed).IsEmpty();
        await Assert.That(result.Usage).IsNull();
    }

    [Test]
    public async Task Reports_a_doc_too_big_for_the_prompt()
    {
        var chat = new FakeChat(FakeChat.Answer(Empty));

        var result = await Run(VnetPlan, chat, NoViolations, docs: Tagging + new string('x', 30_000));

        await Assert.That(result.OmittedDocs!).IsEquivalentTo(["ADR-002"]);
    }

    [Test]
    public async Task Totals_usage_across_attempts()
    {
        var chat = new FakeChat(FakeChat.Answer("not json", 100, 10), FakeChat.Answer(Empty, 200, 20));

        var result = await Run(VnetPlan, chat, NoViolations);

        await Assert.That(result.Usage!.InputTokens).IsEqualTo(300);
        await Assert.That(result.Usage.OutputTokens).IsEqualTo(30);
        await Assert.That(result.Usage.Calls).IsEqualTo(2);
        await Assert.That(result.Usage.Model).IsEqualTo("test-model");
    }

    [Test]
    public async Task Links_only_the_docs_it_cites()
    {
        var result = await Run(VnetPlan, new FakeChat(FakeChat.Answer(Answer(Vnet))), NoViolations);

        await Assert.That(result.StandardsRepository).IsEqualTo("owner/standards");
        await Assert.That(result.DocPaths!["ADR-002"]).IsEqualTo("adr/ADR-002.md");
        await Assert.That(result.DocPaths.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Logs_the_prompt_version_per_call_and_no_plan_text()
    {
        var lines = new List<string>();

        await Run(VnetPlan, new FakeChat(FakeChat.Answer(Answer(Vnet))), NoViolations, log: lines.Add);

        await Assert.That(lines.Single()).Contains($"prompt v{Prompts.Load(ConventionsAgent.Name).Version}");
        await Assert.That(lines.Single()).DoesNotContain("vnet-a-prod");
        await Assert.That(lines.Single()).DoesNotContain("Model title");
    }

    [Test]
    public async Task The_result_survives_the_trip_to_the_publish_job()
    {
        var result = await Run(VnetPlan, new FakeChat(FakeChat.Answer(Answer(Vnet), 5, 6)), TagViolation);

        var parsed = ReviewResult.Parse(JsonSerializer.Serialize(result));

        await Assert.That(parsed.Findings.Single().Deterministic).IsTrue();
        await Assert.That(parsed.DocPaths!["ADR-002"]).IsEqualTo("adr/ADR-002.md");
        await Assert.That(parsed.Usage!.Model).IsEqualTo("test-model");
    }

    private static async Task<ReviewResult> Run(
        string plan, FakeChat chat, string conftestOutput, string docs = Tagging, string config = Config, Action<string>? log = null)
    {
        var directory = Directory.CreateTempSubdirectory("tfreview-source-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "adr"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "adr", "ADR-002.md"), docs);

            var runner = new ReviewRunner(
                new LocalDirectorySource(directory.FullName),
                new ConftestRunner(new FakeProcesses(conftestOutput)),
                new ReviewModel(chat, "test-model"),
                log: log);

            return await runner.RunAsync(plan, config, Commit);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
