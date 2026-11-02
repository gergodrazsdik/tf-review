using System.Net;
using System.Text;
using TfReview.Service;

namespace TfReview.Api.Tests;

public class ReviewSubmitterTests
{
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public async Task An_accepted_review_stops_there()
    {
        var run = new Run(Accepted());

        await Assert.That(await run.SubmitAsync()).IsTrue();

        await Assert.That(run.Handler.Calls).IsEqualTo(1);
        await Assert.That(run.Waited).IsEmpty();
    }

    [Test]
    public async Task It_sends_what_the_api_expects()
    {
        var run = new Run(Accepted());

        await run.SubmitAsync();

        await Assert.That(run.Handler.Paths.Single()).IsEqualTo("/reviews");
        await Assert.That(run.Handler.Bearers.Single()).IsEqualTo("token-1");
        await Assert.That(run.Handler.Bodies.Single()).Contains($"\"commit\":\"{Commit}\"");
        await Assert.That(run.Handler.Bodies.Single()).Contains("\"config_path\":\".tf-review.yml\"");
        await Assert.That(run.Handler.Bodies.Single()).Contains("\"plan\":\"the plan\"");
    }

    // The API is scaling up from nothing, which is what happens on most PRs.
    [Test]
    public async Task It_retries_through_a_cold_start_with_a_new_token()
    {
        var run = new Run(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.BadGateway), Accepted());

        await Assert.That(await run.SubmitAsync()).IsTrue();

        await Assert.That(run.Handler.Calls).IsEqualTo(3);
        await Assert.That(run.Handler.Bearers).IsEquivalentTo(["token-1", "token-2", "token-3"]);
        await Assert.That(run.Waited.Count).IsEqualTo(2);
    }

    [Test]
    public async Task It_retries_when_the_connection_fails_or_times_out()
    {
        var run = new Run(
            _ => throw new HttpRequestException("refused"),
            _ => throw new TaskCanceledException("timed out"),
            Accepted());

        await Assert.That(await run.SubmitAsync()).IsTrue();

        await Assert.That(run.Handler.Calls).IsEqualTo(3);
    }

    [Test]
    public async Task It_gives_up_after_about_five_minutes()
    {
        var run = new Run(Enumerable.Repeat(Status(HttpStatusCode.ServiceUnavailable), 50).ToArray());

        await Assert.That(await run.SubmitAsync()).IsFalse();

        var waited = TimeSpan.FromTicks(run.Waited.Sum(wait => wait.Ticks));
        await Assert.That(waited).IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(4));
        await Assert.That(waited).IsLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.RequestEntityTooLarge)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    public async Task A_refusal_isnt_retried(HttpStatusCode status)
    {
        var run = new Run(Status(status), Accepted());

        await Assert.That(await run.SubmitAsync()).IsFalse();

        await Assert.That(run.Handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_refusal_says_which_rule_failed()
    {
        var run = new Run(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"refused":"UnreleasedCommit"}""", Encoding.UTF8, "application/json"),
        });

        await run.SubmitAsync();

        await Assert.That(run.Log.Single()).Contains("403").And.Contains("UnreleasedCommit");
    }

    // A token failure is GitHub having a moment, the same as the API being away.
    [Test]
    public async Task It_retries_when_the_token_cant_be_fetched()
    {
        var run = new Run(Accepted()) { FailFirstToken = true };

        await Assert.That(await run.SubmitAsync()).IsTrue();

        await Assert.That(run.Waited.Count).IsEqualTo(1);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) => _ => new HttpResponseMessage(status);

    private static Func<HttpRequestMessage, HttpResponseMessage> Accepted() => _ => new HttpResponseMessage(HttpStatusCode.Accepted)
    {
        Content = new StringContent("""{"id":"abc"}""", Encoding.UTF8, "application/json"),
    };

    private sealed class Run(params Func<HttpRequestMessage, HttpResponseMessage>[] answers)
    {
        private int tokens;

        public FakeApi Handler { get; } = new(answers);

        public List<TimeSpan> Waited { get; } = [];

        public List<string> Log { get; } = [];

        public bool FailFirstToken { get; init; }

        public Task<bool> SubmitAsync()
        {
            var http = new HttpClient(this.Handler) { BaseAddress = new Uri("https://api.example.test/") };
            var submitter = new ReviewSubmitter(
                http,
                _ =>
                {
                    this.tokens++;
                    return this.FailFirstToken && this.tokens == 1
                        ? throw new HttpRequestException("token")
                        : Task.FromResult($"token-{this.tokens}");
                },
                (wait, _) =>
                {
                    this.Waited.Add(wait);
                    return Task.CompletedTask;
                },
                this.Log.Add);
            return submitter.SubmitAsync("the plan", Commit, ".tf-review.yml");
        }
    }

    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Paths { get; } = [];

        public List<string> Bearers { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            this.Bearers.Add(request.Headers.Authorization!.Parameter!);
            this.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var answer = answers[Math.Min(this.Calls, answers.Length - 1)];
            this.Calls++;
            return answer(request);
        }
    }
}
