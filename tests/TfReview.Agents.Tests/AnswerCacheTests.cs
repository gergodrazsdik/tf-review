using Microsoft.Extensions.AI;
using TfReview.Core;

namespace TfReview.Agents.Tests;

/// <summary>The result cache (D53), through the smallest role that calls the model.</summary>
public class AnswerCacheTests
{
    private static readonly CondensedPlan Plan = TestPlans.Condensed(
        TestPlans.Create("a.first", "a", "{}"), TestPlans.Create("a.second", "a", "{}"));

    private const string Ranked = """
        {"resources":[{"resource_address":"a.second","reason":"People depend on this."},{"resource_address":"a.first","reason":null}]}
        """;

    [Test]
    public async Task The_same_input_is_answered_without_the_model()
    {
        var chat = new FakeChat(FakeChat.Answer(Ranked, 100, 10));
        var answers = new MemoryAnswers();

        var first = await Rank(chat, answers);
        var again = await Rank(chat, answers);

        await Assert.That(chat.Requests).Count().IsEqualTo(1);
        await Assert.That(again.Order.Select(item => item.ResourceAddress)).IsEquivalentTo(
            first.Order.Select(item => item.ResourceAddress), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(again.Calls.Single().Finish).IsEqualTo(AnswerCache.Finish);
        await Assert.That(again.Calls.Single().InputTokens).IsEqualTo(0);
    }

    [Test]
    public async Task A_changed_input_asks_the_model_again()
    {
        var chat = new FakeChat(FakeChat.Answer(Ranked), FakeChat.Answer(Ranked));
        var answers = new MemoryAnswers();

        await Rank(chat, answers);
        await new RiskOrchestrator(Cached(chat, answers)).RankAsync(
            TestPlans.Condensed(TestPlans.Create("a.first", "a", "{}"), TestPlans.Create("a.third", "a", "{}")));

        await Assert.That(chat.Requests).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Another_deployment_doesnt_share_answers()
    {
        var chat = new FakeChat(FakeChat.Answer(Ranked), FakeChat.Answer(Ranked));
        var answers = new MemoryAnswers();

        await Rank(chat, answers);
        await new RiskOrchestrator(new AnswerCache(chat, answers, "repo", "other-model")).RankAsync(Plan);

        await Assert.That(chat.Requests).Count().IsEqualTo(2);
    }

    // A cached failure would come back on every push for a week, and the retry would get it too.
    [Test]
    public async Task Only_an_answer_that_parsed_is_kept()
    {
        var chat = new FakeChat(FakeChat.Answer("not json"), FakeChat.Answer("not json"));
        var answers = new MemoryAnswers();

        var ranking = await Rank(chat, answers);

        await Assert.That(ranking.Calls).Count().IsEqualTo(2);
        await Assert.That(answers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_stored_answer_that_no_longer_parses_is_a_miss()
    {
        var answers = new MemoryAnswers();
        await Rank(new FakeChat(FakeChat.Answer(Ranked)), answers);
        answers.Overwrite("{\"old\":true}");
        var chat = new FakeChat(FakeChat.Answer(Ranked));

        var ranking = await Rank(chat, answers);

        await Assert.That(chat.Requests).Count().IsEqualTo(1);
        await Assert.That(ranking.Order[0].ResourceAddress).IsEqualTo("a.second");
    }

    // The cache can only ever cost a call: a store that fails is a miss, never a failed review.
    [Test]
    public async Task A_failing_store_still_gets_a_review()
    {
        var chat = new FakeChat(FakeChat.Answer(Ranked));
        var log = new List<string>();

        var ranking = await new RiskOrchestrator(new AnswerCache(chat, new BrokenAnswers(), "repo", "test-model", log.Add)).RankAsync(Plan);

        await Assert.That(ranking.Order[0].ResourceAddress).IsEqualTo("a.second");
        await Assert.That(log).IsEquivalentTo(["answer cache: read failed, IOException.", "answer cache: write failed, IOException."]);
    }

    [Test]
    public async Task The_store_never_holds_the_prompt()
    {
        var chat = new FakeChat(FakeChat.Answer(Ranked));
        var answers = new MemoryAnswers();

        await Rank(chat, answers);

        await Assert.That(answers.Keys.Single()).Matches("^[0-9a-f]{64}$");
    }

    private static AnswerCache Cached(IChatClient chat, IAnswerStore answers) => new(chat, answers, "repo", "test-model");

    private static Task<RiskRanking> Rank(FakeChat chat, MemoryAnswers answers) => new RiskOrchestrator(Cached(chat, answers)).RankAsync(Plan);

    private sealed class BrokenAnswers : IAnswerStore
    {
        public Task<string?> ReadAsync(string scope, string key, CancellationToken cancellationToken) => throw new IOException();

        public Task WriteAsync(string scope, string key, string role, string answer, CancellationToken cancellationToken) => throw new IOException();
    }
}
