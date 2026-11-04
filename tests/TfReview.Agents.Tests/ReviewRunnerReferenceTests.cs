using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents.Tests;

/// <summary>Reference material reaches agents only when a config asks for it, and never decides a finding (D42).</summary>
public class ReviewRunnerReferenceTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Vnet = "azurerm_virtual_network.main";
    private const string Empty = """{"policy_suggestions":[],"findings":[]}""";

    // The re-ranker's answer, which comes before the agent's when there are references to choose from.
    private const string Picked = """{"ids":["REF-0123456789abcdef0123456789abcdef"]}""";
    private const string NoViolations = """[{"namespace":"adr002","successes":1}]""";

    private const string Config = """
        version: 1
        standards:
          repository: owner/standards
          commit: 0123456789abcdef0123456789abcdef01234567
        """;

    private const string LockFile = """
        provider "registry.opentofu.org/hashicorp/azurerm" {
          version     = "5.12.0"
        }
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
        Every resource has owner, environment and project tags.
        """;

    private static readonly string VnetPlan = TestPlans.Json(
        TestPlans.Create(Vnet, "azurerm_virtual_network", """{"name":"vnet-a-prod","address_space":["10.0.0.0/16"],"tags":null}"""));

    private static readonly ReferenceDoc Doc = new(
        "REF-0123456789abcdef0123456789abcdef", "azurerm-docs", "azurerm_virtual_network", "Arguments Reference",
        "CANARY text about address_space", "https://github.com/x", "MPL-2.0", "HashiCorp, MPL-2.0", ["azurerm_virtual_network"]);

    [Test]
    public async Task A_config_that_asks_gets_references_in_their_own_fence()
    {
        var chat = new FakeChat(FakeChat.Answer(Picked), FakeChat.Answer(Empty));
        var retriever = new FakeRetriever(new ReferenceRetrieval([Doc], "0123456789abcdef", false));

        var result = await Run(chat, retriever, Config + "\nreferences: true\n");

        var user = chat.Requests.Last().User;
        await Assert.That(user).Contains("<reference_material>").And.Contains("CANARY text about address_space");
        await Assert.That(user.IndexOf("</reference_material>", StringComparison.Ordinal)).IsLessThan(user.IndexOf("<plan_data>", StringComparison.Ordinal));
        await Assert.That(chat.Requests.Count).IsEqualTo(2);
        await Assert.That(result.References!.CorpusVersion).IsEqualTo("0123456789abcdef");
        await Assert.That(result.References.Attributions.Single()).IsEqualTo("HashiCorp, MPL-2.0");
    }

    [Test]
    public async Task The_query_holds_types_and_attribute_names_and_the_lock_files_version()
    {
        var retriever = new FakeRetriever(ReferenceRetrieval.None);

        await Run(new FakeChat(FakeChat.Answer(Empty)), retriever, Config + "\nreferences: true\n");

        var query = retriever.Queries.Single();
        await Assert.That(query.ResourceTypes.Single()).IsEqualTo("azurerm_virtual_network");
        await Assert.That(query.ProviderVersion).IsEqualTo("5.12.0");
        await Assert.That(query.Text).Contains("address_space");
        await Assert.That(query.Text).DoesNotContain("10.0.0.0");
        await Assert.That(query.Candidates).IsEqualTo(ReferenceSelector.Pool);
    }

    [Test]
    public async Task A_config_that_doesnt_ask_gets_none_and_the_index_isnt_touched()
    {
        var chat = new FakeChat(FakeChat.Answer(Empty));
        var retriever = new FakeRetriever(new ReferenceRetrieval([Doc], "0123456789abcdef", false));

        var result = await Run(chat, retriever, Config);

        await Assert.That(retriever.Queries).IsEmpty();
        await Assert.That(chat.Requests.Single().User).DoesNotContain("reference_material");
        await Assert.That(result.References).IsNull();
    }

    [Test]
    public async Task A_failed_search_costs_the_references_and_not_the_review()
    {
        var chat = new FakeChat(FakeChat.Answer(Empty));
        var log = new List<string>();

        var result = await Run(chat, new FakeRetriever(null), Config + "\nreferences: true\n", log.Add);

        await Assert.That(result.Verdict).IsEqualTo(Verdict.NoFindings);
        await Assert.That(result.NotReviewed).IsEmpty();
        await Assert.That(chat.Requests.Single().User).DoesNotContain("reference_material");
        await Assert.That(log.Any(line => line.Contains("references unavailable", StringComparison.Ordinal))).IsTrue();
    }

    // D42 in code: an agent citing what it was shown as background is dropped by the evidence check.
    [Test]
    public async Task A_finding_citing_a_reference_is_dropped()
    {
        var answer = $$"""
            {"policy_suggestions":[],"findings":[{"resource_address":"{{Vnet}}","doc_id":"{{Doc.Id}}","attribute":"address_space","title":"Too wide.","suggestion":null,"breaks_standard":true}]}
            """;
        var chat = new FakeChat(FakeChat.Answer(Picked), FakeChat.Answer(answer));

        var result = await Run(chat, new FakeRetriever(new ReferenceRetrieval([Doc], "0123456789abcdef", false)), Config + "\nreferences: true\n");

        await Assert.That(result.Findings).IsEmpty();
    }

    // D50: per finding, the agent's prompt holds no references, and nothing is searched when there's no finding.
    [Test]
    public async Task Per_finding_the_agent_sees_no_references()
    {
        var retriever = new FakeRetriever(new ReferenceRetrieval([Doc], "0123456789abcdef", false));
        var chat = new FakeChat(FakeChat.Answer(Empty));

        var result = await Run(chat, retriever, Config + "\nreferences: true\n", placement: ReferencePlacement.Finding);

        await Assert.That(chat.Requests.Single().User).DoesNotContain("reference_material");
        await Assert.That(retriever.Queries).IsEmpty();
        await Assert.That(result.References).IsNotNull();
    }

    // Per agent unless a test says otherwise, because most of these are about what an agent's prompt holds.
    private static async Task<ReviewResult> Run(
        FakeChat chat, IReferenceRetriever retriever, string config, Action<string>? log = null, ReferencePlacement placement = ReferencePlacement.Agent)
    {
        var directory = Directory.CreateTempSubdirectory("tfreview-source-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "adr"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "adr", "ADR-002.md"), Tagging);

            // The rule-based arbiter, so the script only needs the agent's answer.
            var runner = new ReviewRunner(
                new LocalDirectorySource(directory.FullName),
                new ConftestRunner(new FakeProcesses(NoViolations)),
                new ReviewModel(chat, "test-model"),
                arbiter: new RuleArbiter(),
                log: log,
                references: retriever,
                placement: placement);

            return await runner.RunAsync(VnetPlan, config, Commit, lockFile: LockFile);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeRetriever(ReferenceRetrieval? result) : IReferenceRetriever
    {
        public List<ReferenceQuery> Queries { get; } = [];

        public Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default)
        {
            this.Queries.Add(query);
            return result is null ? throw new HttpRequestException("Cosmos is away.") : Task.FromResult(result);
        }
    }
}
