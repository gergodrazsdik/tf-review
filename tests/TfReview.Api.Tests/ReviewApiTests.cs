using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TfReview.Service;

namespace TfReview.Api.Tests;

/// <summary>Real tokens signed by a test key, through the real validator and the real endpoints, with fake storage.</summary>
public class ReviewApiTests
{
    private const string Released = "393013f60b4a7faea92f330eee4630bfbe3d2b18";
    private const string Unreleased = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Workflow = "gergodrazsdik/tf-review/.github/workflows/tf-review.yml";

    private const string CleanPlan = """
        {"format_version":"1.2","terraform_version":"1.13.2","planned_values":{"root_module":{}},"resource_changes":[],"configuration":{}}
        """;

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "test" };

    // What the spike saw on the demo repo's review job, the only kind of job that gets in.
    private static Dictionary<string, object> GoodClaims() => new()
    {
        ["event_name"] = "pull_request",
        ["environment"] = "review",
        ["repository_id"] = "1441359044",
        ["repository"] = "gergodrazsdik/tf-review-demo",
        ["job_workflow_ref"] = $"{Workflow}@{Released}",
        ["ref"] = "refs/pull/7/merge",
        ["run_id"] = "100",
        ["run_attempt"] = "1",
    };

    [Test]
    public async Task The_review_job_of_a_released_workflow_gets_in()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var job = api.Queue.Jobs.Single();
        await Assert.That(job.Repository).IsEqualTo("gergodrazsdik/tf-review-demo");
        await Assert.That(job.RepositoryId).IsEqualTo("1441359044");
        await Assert.That(job.PullRequest).IsEqualTo(7);
        await Assert.That(job.Commit).IsEqualTo(Commit);
        await Assert.That(api.Plans.Saved.ContainsKey(job.ReviewId)).IsTrue();
    }

    [Test]
    [Arguments("event_name", "push", "WrongEvent")]
    [Arguments("event_name", "pull_request_target", "WrongEvent")]
    [Arguments("environment", "prod", "WrongEnvironment")]
    [Arguments("repository_id", "999", "RepositoryNotAllowed")]
    [Arguments("job_workflow_ref", "attacker/tf-review/.github/workflows/tf-review.yml@393013f60b4a7faea92f330eee4630bfbe3d2b18", "WrongWorkflow")]
    [Arguments("job_workflow_ref", "gergodrazsdik/tf-review-demo/.github/workflows/plan.yml@393013f60b4a7faea92f330eee4630bfbe3d2b18", "WrongWorkflow")]
    [Arguments("job_workflow_ref", "gergodrazsdik/tf-review/.github/workflows/e2e-live.yml@393013f60b4a7faea92f330eee4630bfbe3d2b18", "WrongWorkflow")]
    [Arguments("job_workflow_ref", "gergodrazsdik/tf-review/.github/workflows/tf-review.yml@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "UnreleasedCommit")]
    [Arguments("job_workflow_ref", "gergodrazsdik/tf-review/.github/workflows/tf-review.yml@refs/tags/v0.0.8", "UnreleasedCommit")]
    [Arguments("job_workflow_ref", "gergodrazsdik/tf-review/.github/workflows/tf-review.yml@refs/heads/main", "UnreleasedCommit")]
    [Arguments("ref", "refs/heads/main", "NotAPullRequest")]
    [Arguments("ref", "refs/pull/7/head", "NotAPullRequest")]
    [Arguments("ref", "refs/pull/x/merge", "NotAPullRequest")]
    public async Task A_valid_token_from_the_wrong_kind_of_job_is_refused(string claim, string value, string refusal)
    {
        await using var api = await Api.StartAsync();
        var claims = GoodClaims();
        claims[claim] = value;

        using var response = await api.SubmitAsync(Token(claims));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("refused").GetString()).IsEqualTo(refusal);
        await Assert.That(api.Queue.Jobs).IsEmpty();
        await Assert.That(api.Plans.Saved).IsEmpty();
    }

    // Real claims from the OIDC spike: PR code's plan job has no environment.
    [Test]
    [Arguments("event_name", "WrongEvent")]
    [Arguments("environment", "WrongEnvironment")]
    [Arguments("repository_id", "RepositoryNotAllowed")]
    [Arguments("job_workflow_ref", "WrongWorkflow")]
    [Arguments("ref", "NotAPullRequest")]
    [Arguments("repository", "IncompleteToken")]
    [Arguments("run_id", "IncompleteToken")]
    [Arguments("run_attempt", "IncompleteToken")]
    public async Task A_token_missing_a_claim_is_refused(string claim, string refusal)
    {
        await using var api = await Api.StartAsync();
        var claims = GoodClaims();
        claims.Remove(claim);

        using var response = await api.SubmitAsync(Token(claims));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("refused").GetString()).IsEqualTo(refusal);
    }

    [Test]
    public async Task A_token_signed_by_someone_else_is_refused()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims(), key: OtherKey));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(api.Queue.Jobs).IsEmpty();
    }

    [Test]
    public async Task A_token_from_another_issuer_is_refused()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims(), issuer: "https://token.actions.githubusercontent.com.evil.example"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    // The default audience is what any job can use with Azure, and it mustn't get in here.
    [Test]
    [Arguments("api://AzureADTokenExchange")]
    [Arguments("https://github.com/gergodrazsdik")]
    public async Task A_token_for_another_audience_is_refused(string audience)
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims(), audience: audience));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task An_expired_token_is_refused()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims(), expires: DateTime.UtcNow.AddMinutes(-10)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task An_unsigned_token_is_refused()
    {
        await using var api = await Api.StartAsync();
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = GitHubTokens.Issuer,
            Audience = ApiOptions.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = GoodClaims(),
        });

        using var response = await api.SubmitAsync(unsigned);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task No_token_is_refused()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.Client.PostAsJsonAsync("/reviews", Body());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task The_pull_request_comes_from_the_token_and_the_request_cant_add_one()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SendAsync(
            Token(GoodClaims()),
            new { commit = Commit, config_path = ".tf-review.yml", plan = CleanPlan, pull_request = 99 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(api.Queue.Jobs).IsEmpty();
    }

    [Test]
    [Arguments("not-a-commit", ".tf-review.yml")]
    [Arguments("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", ".tf-review.yml")]
    [Arguments(Commit, "../secrets.yml")]
    [Arguments(Commit, "a/../../b.yml")]
    [Arguments(Commit, "/etc/passwd")]
    [Arguments(Commit, "a b.yml")]
    public async Task A_bad_request_is_refused(string commit, string configPath)
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SendAsync(Token(GoodClaims()), new { commit, config_path = configPath, plan = CleanPlan });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(api.Queue.Jobs).IsEmpty();
    }

    [Test]
    public async Task The_plan_is_redacted_again_before_it_is_stored()
    {
        await using var api = await Api.StartAsync();
        const string secret = "CANARY-api-secret";
        var plan = $$$$$"""
            {"format_version":"1.2","terraform_version":"1.13.2","planned_values":{"root_module":{}},"configuration":{},
            "resource_changes":[{"address":"azurerm_key_vault_secret.a","mode":"managed","type":"azurerm_key_vault_secret","name":"a",
            "change":{"actions":["create"],"before":null,"after":{"value":"{{{{{secret}}}}}"},"after_unknown":{},
            "before_sensitive":false,"after_sensitive":{"value":true}}}]}
            """;

        using var response = await api.SubmitAsync(Token(GoodClaims()), plan);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var stored = api.Plans.Saved.Values.Single();
        await Assert.That(stored).DoesNotContain(secret);
        await Assert.That(stored).Contains("azurerm_key_vault_secret.a");
    }

    // The PR still has to get an answer, so a refused plan goes on to the worker as a refusal.
    [Test]
    public async Task A_plan_it_refuses_still_gets_a_review_that_says_so()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.SubmitAsync(Token(GoodClaims()), "not json at all CANARY");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(api.Plans.Saved.Values.Single()).IsEqualTo("""{"refused":true}""");
        await Assert.That(api.Queue.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_retry_is_the_same_review()
    {
        await using var api = await Api.StartAsync();

        using var first = await api.SubmitAsync(Token(GoodClaims()));
        using var second = await api.SubmitAsync(Token(GoodClaims()));

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await Id(second)).IsEqualTo(await Id(first));
        await Assert.That(api.Queue.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_re_run_is_a_new_review()
    {
        await using var api = await Api.StartAsync();
        var rerun = GoodClaims();
        rerun["run_attempt"] = "2";

        using var first = await api.SubmitAsync(Token(GoodClaims()));
        using var second = await api.SubmitAsync(Token(rerun));

        await Assert.That(await Id(second)).IsNotEqualTo(await Id(first));
        await Assert.That(api.Queue.Jobs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Too_large_a_request_is_refused_before_anything_is_stored()
    {
        await using var api = await Api.StartAsync(new ApiOptions(Allowed, ReleasedCommits) { MaxRequestBytes = 1_000 });

        using var response = await api.SubmitAsync(Token(GoodClaims()), new string('x', 2_000));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(api.Plans.Saved).IsEmpty();
    }

    [Test]
    public async Task One_repo_can_only_use_its_own_rate_limit()
    {
        await using var api = await Api.StartAsync(new ApiOptions(Allowed, ReleasedCommits) { ReviewsPerHour = 1 });
        var other = GoodClaims();
        other["repository_id"] = "1441358991";

        using var first = await api.SubmitAsync(Token(GoodClaims()));
        using var limited = await api.SubmitAsync(Token(GoodClaims()));
        using var otherRepo = await api.SubmitAsync(Token(other));

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(otherRepo.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task A_refused_caller_doesnt_use_up_a_repos_rate_limit()
    {
        await using var api = await Api.StartAsync(new ApiOptions(Allowed, ReleasedCommits) { ReviewsPerHour = 1 });
        var attacker = GoodClaims();
        attacker["environment"] = "prod";

        using var refused = await api.SubmitAsync(Token(attacker));
        using var real = await api.SubmitAsync(Token(GoodClaims()));

        await Assert.That(real.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    }

    [Test]
    [Arguments("plans")]
    [Arguments("queue")]
    [Arguments("statuses")]
    public async Task A_storage_failure_tells_the_workflow_to_retry(string failing)
    {
        await using var api = await Api.StartAsync();
        api.FailIn(failing);

        using var response = await api.SubmitAsync(Token(GoodClaims()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEmpty();
    }

    [Test]
    public async Task A_retry_after_a_failure_finishes_the_review()
    {
        await using var api = await Api.StartAsync();
        api.FailIn("queue");
        using var failed = await api.SubmitAsync(Token(GoodClaims()));
        api.FailIn(null);

        using var retried = await api.SubmitAsync(Token(GoodClaims()));

        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(api.Queue.Jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_repo_can_read_its_own_review()
    {
        await using var api = await Api.StartAsync();
        using var submitted = await api.SubmitAsync(Token(GoodClaims()));
        var id = await Id(submitted);

        using var response = await api.GetAsync(Token(GoodClaims()), id);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("state").GetString()).IsEqualTo("queued");
        await Assert.That(body.GetProperty("commit").GetString()).IsEqualTo(Commit);
        await Assert.That(body.TryGetProperty("plan", out _)).IsFalse();
    }

    [Test]
    public async Task A_repo_cant_read_another_repos_review()
    {
        await using var api = await Api.StartAsync();
        using var submitted = await api.SubmitAsync(Token(GoodClaims()));
        var other = GoodClaims();
        other["repository_id"] = "1441358991";

        using var response = await api.GetAsync(Token(other), await Id(submitted));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Reading_a_review_needs_a_token()
    {
        await using var api = await Api.StartAsync();
        using var submitted = await api.SubmitAsync(Token(GoodClaims()));

        using var response = await api.Client.GetAsync($"/reviews/{await Id(submitted)}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_malformed_review_id_is_not_found()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.GetAsync(Token(GoodClaims()), "..%2F..%2Fsecrets");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task The_health_check_needs_no_token()
    {
        await using var api = await Api.StartAsync();

        using var response = await api.Client.GetAsync("/healthz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static readonly HashSet<string> Allowed = ["1441358991", "1441359044"];
    private static readonly HashSet<string> ReleasedCommits = [Released];

    private static object Body(string plan = CleanPlan) => new { commit = Commit, config_path = ".tf-review.yml", plan };

    private static async Task<string> Id(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

    private static string Token(
        Dictionary<string, object> claims,
        SecurityKey? key = null,
        string issuer = GitHubTokens.Issuer,
        string audience = ApiOptions.Audience,
        DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = expiry.AddMinutes(-10),
            IssuedAt = expiry.AddMinutes(-10),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly WebApplication app;

        private Api(WebApplication app, FakePlans plans, FakeQueue queue, FakeStatuses statuses)
        {
            this.app = app;
            this.Plans = plans;
            this.Queue = queue;
            this.Statuses = statuses;
            this.Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public FakePlans Plans { get; }

        public FakeQueue Queue { get; }

        public FakeStatuses Statuses { get; }

        public static async Task<Api> StartAsync(ApiOptions? options = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();

            var plans = new FakePlans();
            var queue = new FakeQueue();
            var statuses = new FakeStatuses();
            var tokens = new GitHubTokens(GitHubTokens.Parameters(
                ApiOptions.Audience, GitHubTokens.Issuer, parameters => parameters.IssuerSigningKey = Key));
            app.MapReviews(options ?? new ApiOptions(Allowed, ReleasedCommits), tokens, new ServiceStores(plans, queue, statuses));

            await app.StartAsync();
            return new Api(app, plans, queue, statuses);
        }

        public void FailIn(string? store)
        {
            this.Plans.Fail = store == "plans";
            this.Queue.Fail = store == "queue";
            this.Statuses.Fail = store == "statuses";
        }

        public Task<HttpResponseMessage> SubmitAsync(string token, string plan = CleanPlan) => this.SendAsync(token, Body(plan));

        public Task<HttpResponseMessage> SendAsync(string token, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/reviews") { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return this.Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> GetAsync(string token, string id)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/reviews/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return this.Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            await this.app.DisposeAsync();
        }
    }

    private sealed class FakePlans : IPlanStore
    {
        public ConcurrentDictionary<string, string> Saved { get; } = new();

        public bool Fail { get; set; }

        public Task SaveAsync(string reviewId, string plan, CancellationToken cancellationToken = default)
        {
            FailIf(this.Fail);
            this.Saved[reviewId] = plan;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string reviewId, CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Saved.GetValueOrDefault(reviewId));
    }

    private sealed class FakeQueue : IReviewQueue
    {
        public List<ReviewJob> Jobs { get; } = [];

        public bool Fail { get; set; }

        public Task EnqueueAsync(ReviewJob job, CancellationToken cancellationToken = default)
        {
            FailIf(this.Fail);
            this.Jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<ReceivedJob?> ReceiveAsync(TimeSpan visibility, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReceivedJob?>(null);
    }

    private sealed class FakeStatuses : IReviewStatusStore
    {
        private readonly ConcurrentDictionary<(string Repository, string Id), ReviewStatus> statuses = new();

        public bool Fail { get; set; }

        public Task<ReviewStatus?> ReadAsync(string repositoryId, string reviewId, CancellationToken cancellationToken = default)
        {
            FailIf(this.Fail);
            return Task.FromResult(this.statuses.GetValueOrDefault((repositoryId, reviewId)));
        }

        public Task WriteAsync(ReviewStatus status, CancellationToken cancellationToken = default)
        {
            FailIf(this.Fail);
            this.statuses[(status.RepositoryId, status.ReviewId)] = status;
            return Task.CompletedTask;
        }
    }

    private static void FailIf(bool fail)
    {
        if (fail)
        {
            throw new InvalidOperationException("CANARY storage failure");
        }
    }
}
