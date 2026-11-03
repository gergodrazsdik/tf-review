using TfReview.Service;

namespace TfReview.Api.Tests;

public class ReferenceSearchTermsTests
{
    [Test]
    public async Task Terms_are_the_words_that_say_what_a_query_is_about()
    {
        var terms = CosmosReferenceRetriever.SearchTerms("azurerm_storage_account changed attributes: min_tls_version, allow_nested_items_to_be_public");

        await Assert.That(terms).Contains("storage").And.Contains("tls").And.Contains("public");
        await Assert.That(terms).DoesNotContain("azurerm").And.DoesNotContain("changed").And.DoesNotContain("to");
    }

    // Each term becomes a query parameter, so a long query can't build an unbounded one.
    [Test]
    public async Task There_are_never_more_than_twelve_terms()
    {
        var text = string.Join(' ', Enumerable.Range(0, 50).Select(index => $"word{index}"));

        await Assert.That(CosmosReferenceRetriever.SearchTerms(text).Count).IsEqualTo(12);
    }
}
