using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TfReview.GitHub.Tests;

public class StickyCommentTests
{
    private const string Repository = "owner/repo";
    private const int PullRequest = 7;

    [Test]
    public async Task Creates_a_comment_when_there_is_none()
    {
        var github = new FakeGitHub();

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Created);
        await Assert.That(github.Writes.Single().Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(github.Writes.Single().Path).IsEqualTo("/repos/owner/repo/issues/7/comments");
        await Assert.That(github.Writes.Single().Body).IsEqualTo("new body");
    }

    // The service posts as the App, and the workflow as itself when the service is down (D39). Neither can leave
    // the other's older result standing.
    [Test]
    [Arguments("tf-review-gergodrazsdik[bot]")]
    [Arguments("github-actions[bot]")]
    public async Task Updates_a_comment_from_either_of_its_identities(string login)
    {
        var github = new FakeGitHub(Comment(42, login, Marked));

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Updated);
        await Assert.That(github.Writes.Single().Path).IsEqualTo("/repos/owner/repo/issues/comments/42");
    }

    [Test]
    public async Task Ignores_the_marker_from_another_bot()
    {
        var github = new FakeGitHub(Comment(42, "someone-else[bot]", Marked));

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Created);
    }

    [Test]
    public async Task Updates_its_own_comment()
    {
        var github = new FakeGitHub(Comment(41, "someone", "hello"), Comment(42, "github-actions[bot]", Marked));

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Updated);
        await Assert.That(github.Writes.Single().Method).IsEqualTo(HttpMethod.Patch);
        await Assert.That(github.Writes.Single().Path).IsEqualTo("/repos/owner/repo/issues/comments/42");
    }

    [Test]
    public async Task Ignores_the_marker_in_someone_elses_comment()
    {
        var github = new FakeGitHub(Comment(41, "attacker", Marked));

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Created);
    }

    [Test]
    public async Task Ignores_its_own_comments_without_the_marker()
    {
        var github = new FakeGitHub(Comment(41, "github-actions[bot]", "an unrelated comment"));

        await Assert.That(await Upsert(github, "new body")).IsEqualTo(CommentAction.Created);
    }

    [Test]
    public async Task Finds_its_comment_on_a_later_page()
    {
        var comments = Enumerable.Range(1, 100).Select(id => Comment(id, "someone", "hello"))
            .Append(Comment(101, "github-actions[bot]", Marked)).ToArray();
        var github = new FakeGitHub(comments);

        var action = await Upsert(github, "new body");

        await Assert.That(action).IsEqualTo(CommentAction.Updated);
        await Assert.That(github.Writes.Single().Path).IsEqualTo("/repos/owner/repo/issues/comments/101");
    }

    [Test]
    public async Task Fails_when_github_rejects_the_write()
    {
        var github = new FakeGitHub { WriteStatus = HttpStatusCode.Forbidden };

        await Assert.That(() => Upsert(github, "new body")).Throws<HttpRequestException>();
    }

    private static string Marked => $"{CommentRenderer.Marker}\nold body";

    private static Task<CommentAction> Upsert(FakeGitHub github, string body)
    {
        var http = new HttpClient(github) { BaseAddress = new Uri("https://api.example.test/") };
        return new StickyComment(http, Repository, PullRequest).UpsertAsync(body);
    }

    private static JsonObject Comment(long id, string login, string body) => new()
    {
        ["id"] = id,
        ["user"] = new JsonObject { ["login"] = login },
        ["body"] = body,
    };

    private sealed class FakeGitHub(params JsonObject[] comments) : HttpMessageHandler
    {
        private const int PageSize = 100;

        public HttpStatusCode WriteStatus { get; init; } = HttpStatusCode.OK;

        public List<(HttpMethod Method, string Path, string? Body)> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var page = int.Parse(request.RequestUri!.Query.Split("page=").Last());
                var items = comments.Skip((page - 1) * PageSize).Take(PageSize)
                    .Select(comment => (JsonNode)comment.DeepClone()).ToArray();
                return Json(new JsonArray(items).ToJsonString());
            }

            var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonDocument.Parse(sent).RootElement.GetProperty("body").GetString();
            Writes.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(WriteStatus) { Content = new StringContent("{}") };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
