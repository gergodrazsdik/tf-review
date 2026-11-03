using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents.Tests;

public class ReferenceSelectorTests
{
    private static readonly CondensedPlan Slice = TestPlans.Condensed(TestPlans.Create(
        "azurerm_network_security_rule.ssh",
        "azurerm_network_security_rule",
        """{"name":"CANARY-name","destination_port_range":"22","description":"CANARY free text that is long enough to leave out","priority":100,"tags":{"owner":"CANARY-tag"},"source_address_prefix":null}"""));

    private static readonly ReferenceQuery Query = new("query", ["azurerm_network_security_rule"], "5.12.0", TokenBudget: 1_000);

    [Test]
    public async Task The_models_picks_go_first_and_the_budget_takes_the_rest_in_the_searchs_order()
    {
        var retriever = new FakeRetriever(Doc("a", 900), Doc("b", 900), Doc("c", 900), Doc("d", 900));
        var chat = new FakeChat(FakeChat.Answer("""{"ids":["REF-c","REF-a"]}"""));

        var selection = await new ReferenceSelector(retriever, chat).SelectAsync(Query, ["security"], Slice);

        await Assert.That(selection.Retrieval.Docs.Select(doc => doc.Id)).IsEquivalentTo(["REF-c", "REF-a", "REF-b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(retriever.Queries.Single().Candidates).IsEqualTo(ReferenceSelector.Pool);
        await Assert.That(selection.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Ids_it_wasnt_offered_are_ignored()
    {
        var chat = new FakeChat(FakeChat.Answer("""{"ids":["REF-elsewhere","REF-b","REF-b"]}"""));

        var selection = await new ReferenceSelector(new FakeRetriever(Doc("a", 10), Doc("b", 10)), chat).SelectAsync(Query, ["security"], Slice);

        await Assert.That(selection.Retrieval.Docs.Select(doc => doc.Id)).IsEquivalentTo(["REF-b", "REF-a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    // Re-ranking is an improvement, not a dependency.
    [Test]
    public async Task A_failed_call_keeps_the_searchs_own_order()
    {
        var chat = new FakeChat(new HttpRequestException("away"));

        var selection = await new ReferenceSelector(new FakeRetriever(Doc("a", 10), Doc("b", 10)), chat).SelectAsync(Query, ["security"], Slice);

        await Assert.That(selection.Retrieval.Docs.Select(doc => doc.Id)).IsEquivalentTo(["REF-a", "REF-b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Without_a_model_it_is_the_search_alone()
    {
        var retriever = new FakeRetriever(Doc("a", 10));

        var selection = await new ReferenceSelector(retriever, null).SelectAsync(Query, ["security"], Slice);

        await Assert.That(selection.Calls).IsEmpty();
        await Assert.That(retriever.Queries.Single()).IsEqualTo(Query);
    }

    // The model sees what a change is, not what a PR wrote: no names, tags or free text.
    [Test]
    public async Task The_model_sees_types_and_short_values_but_none_of_a_prs_own_words()
    {
        var chat = new FakeChat(FakeChat.Answer("""{"ids":[]}"""));

        await new ReferenceSelector(new FakeRetriever(Doc("a", 10)), chat).SelectAsync(Query, ["security"], Slice);

        var user = chat.Requests.Single().User;
        await Assert.That(user).Contains("azurerm_network_security_rule").And.Contains("\"destination_port_range\":\"22\"").And.Contains("\"priority\":100");
        await Assert.That(user).DoesNotContain("CANARY");
        await Assert.That(user).DoesNotContain("source_address_prefix");
    }

    [Test]
    public async Task A_doc_too_big_for_whats_left_is_skipped_for_a_smaller_one()
    {
        var chosen = ReferenceSelector.Fill([Doc("big", 3_600), Doc("small", 300)], 1_000);

        await Assert.That(chosen.Single().Id).IsEqualTo("REF-small");
    }

    private static ReferenceDoc Doc(string id, int characters) => new(
        $"REF-{id}", "azure-policy", $"Title {id}", string.Empty, new string('x', characters), "https://github.com/x", "MIT", "Microsoft, MIT", []);

    private sealed class FakeRetriever(params ReferenceDoc[] docs) : IReferenceRetriever
    {
        public List<ReferenceQuery> Queries { get; } = [];

        public Task<ReferenceRetrieval> RetrieveAsync(ReferenceQuery query, CancellationToken cancellationToken = default)
        {
            this.Queries.Add(query);
            return Task.FromResult(new ReferenceRetrieval(docs, "0123456789abcdef", false));
        }
    }
}
