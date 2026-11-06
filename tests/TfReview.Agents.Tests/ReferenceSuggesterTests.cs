using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

public class ReferenceSuggesterTests
{
    private const string Address = "azurerm_network_security_rule.rdp";

    private static readonly CondensedPlan Plan = TestPlans.Condensed(TestPlans.Create(
        Address, "azurerm_network_security_rule", """{"destination_port_range":"3389","source_address_prefix":"CANARY-Internet"}"""));

    private static readonly StandardsDoc Standard = new(
        "POL-002", StandardsDoc.Accepted, Severity.High, ["azurerm_network_security_rule"], ["security"], "policies/POL-002.md",
        "# POL-002: No admin ports open to the internet\n\nNo inbound rule may allow SSH or RDP from the internet.");

    private static readonly ReferenceDoc Bastion = new(
        "REF-bastion", "caf", "Azure Bastion", "Overview", "Azure Bastion gives RDP and SSH access without a public IP.",
        "https://learn.microsoft.com/x", "CC-BY-4.0", "Microsoft, CC-BY-4.0", []);

    private static Finding Finding(Reporter agent = Reporter.Security, string? suggestion = "Restrict the source to a private range.") =>
        new(Address, "POL-002", Severity.High, agent, Deterministic: true, "RDP is open to the internet.",
            [new PlanEvidence(CondensedPlan.PointerTo(Address, "change", "after", "source_address_prefix")), new DocEvidence("POL-002")])
        {
            Suggestion = suggestion,
        };

    private static ReferenceSuggester.WireOutput Answer(string? term = "Azure Bastion", string? id = "REF-bastion", bool same = true) =>
        new("Use Azure Bastion for RDP instead of opening the port.", id, term, same);

    [Test]
    public async Task A_grounded_sentence_on_the_same_problem_is_kept()
    {
        await Assert.That(ReferenceSuggester.Accept(Answer(), [Bastion], "source_address_prefix", "Restrict it.")).IsNotNull();
    }

    [Test]
    [Arguments("Azure Bastion", "REF-elsewhere", true)]
    [Arguments("Just-in-time access", "REF-bastion", true)]
    [Arguments("Azure Bastion", "REF-bastion", false)]
    public async Task A_sentence_from_no_offered_excerpt_or_naming_what_it_lacks_or_about_another_problem_is_dropped(
        string term, string id, bool same)
    {
        await Assert.That(ReferenceSuggester.Accept(Answer(term, id, same), [Bastion], "source_address_prefix", "Restrict it.")).IsNull();
    }

    // BUG-4: D50 keeps a sentence only if it names the term the excerpt was checked for.
    [Test]
    public async Task A_sentence_that_doesnt_name_its_term_is_dropped()
    {
        var answer = new ReferenceSuggester.WireOutput("Enable boot diagnostics on the VM.", "REF-bastion", "Azure Bastion", true);

        await Assert.That(ReferenceSuggester.Accept(answer, [Bastion], "source_address_prefix", null)).IsNull();
    }

    // BUG-4: punctuation normalises to nothing, which every text contains.
    [Test]
    [Arguments("---")]
    [Arguments("a-b")]
    [Arguments("  !!  ")]
    public async Task A_term_with_too_few_letters_or_digits_is_dropped(string term)
    {
        var answer = new ReferenceSuggester.WireOutput($"Do something unrelated {term}.", "REF-bastion", term, true);

        await Assert.That(ReferenceSuggester.Accept(answer, [Bastion], "source_address_prefix", null)).IsNull();
    }

    // As v6 did with allowBlobPublicAccess for allow_nested_items_to_be_public: the same fix under another name.
    [Test]
    public async Task The_findings_own_attribute_or_a_term_the_suggestion_has_is_dropped()
    {
        var doc = Bastion with { Text = "Set source_address_prefix and use Azure Bastion." };

        await Assert.That(ReferenceSuggester.Accept(Answer("Source-Address-Prefix"), [doc], "source_address_prefix", "Restrict it.")).IsNull();
        await Assert.That(ReferenceSuggester.Accept(Answer(), [doc], "source_address_prefix", "Use azure bastion.")).IsNull();
    }

    [Test]
    public async Task It_adds_to_the_suggestion_and_searches_without_values()
    {
        var retriever = new FakeRetriever(Bastion);
        var chat = new FakeChat(FakeChat.Answer(
            """{"addition":"Use Azure Bastion for RDP instead.","reference_id":"REF-bastion","term":"Azure Bastion","same_problem":true}"""));

        var pass = await new ReferenceSuggester(retriever, chat).SuggestAsync([Finding()], Plan, [Standard], "5.12.0");

        await Assert.That(pass.Findings.Single().Suggestion).IsEqualTo("Restrict the source to a private range. Use Azure Bastion for RDP instead.");
        var query = retriever.Queries.Single();
        await Assert.That(query.Text).Contains("No admin ports open to the internet");
        await Assert.That(query.Text).Contains("source_address_prefix");
        await Assert.That(query.Text).DoesNotContain("CANARY");
        await Assert.That(query.ResourceTypes).IsEquivalentTo(["azurerm_network_security_rule"]);
        await Assert.That(pass.Retrievals.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Scanner_findings_and_failed_calls_are_left_as_they_were()
    {
        var retriever = new FakeRetriever(Bastion);
        var chat = new FakeChat(new HttpRequestException("away"));

        var pass = await new ReferenceSuggester(retriever, chat).SuggestAsync([Finding(Reporter.Scanner), Finding()], Plan, [Standard], null);

        await Assert.That(pass.Findings.Select(finding => finding.Suggestion ?? string.Empty)).IsEquivalentTo(
            ["Restrict the source to a private range.", "Restrict the source to a private range."]);
        await Assert.That(retriever.Queries.Count).IsEqualTo(1);
    }

    // SEC-5, OPS-13: each finding costs a search and a call, so a review gets a fixed number, the most severe first.
    [Test]
    public async Task Only_the_first_findings_get_a_search_and_a_call()
    {
        var retriever = new FakeRetriever();
        var findings = Enumerable.Range(0, ReferenceSuggester.MaxFindings + 5).Select(_ => Finding()).ToList();

        var pass = await new ReferenceSuggester(retriever, new FakeChat()).SuggestAsync(findings, Plan, [Standard], null);

        await Assert.That(retriever.Queries.Count).IsEqualTo(ReferenceSuggester.MaxFindings);
        await Assert.That(pass.Findings.Count).IsEqualTo(findings.Count);
    }

    [Test]
    public async Task A_sentence_recommending_azure_policy_is_dropped()
    {
        var answer = new ReferenceSuggester.WireOutput("Use Azure Policies to allow only approved SKUs.", "REF-bastion", "Azure Bastion", true);

        await Assert.That(ReferenceSuggester.Accept(answer, [Bastion], "size", "Confirm it.")).IsNull();
    }

    private sealed class FakeRetriever(params ReferenceDoc[] docs) : IReferenceRetriever
    {
        public List<ReferenceQuery> Queries { get; } = [];

        public Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            return Task.FromResult(new ReferenceRetrieval(docs, "v1", false));
        }
    }
}
