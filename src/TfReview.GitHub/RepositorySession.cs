using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TfReview.Core;

namespace TfReview.GitHub;

public sealed record PullRequestCommits(string Head, string Base);

/// <summary>What one review does on one repo through its own token, which is revoked when this is disposed.</summary>
public sealed class RepositorySession(HttpClient http, string repository) : IAsyncDisposable
{
    public async Task<PullRequestCommits> ReadPullRequestAsync(int pullRequest, CancellationToken cancellationToken = default)
    {
        var body = await http.GetFromJsonAsync<JsonElement>($"repos/{repository}/pulls/{pullRequest}", cancellationToken);
        return new PullRequestCommits(
            body.GetProperty("head").GetProperty("sha").GetString() ?? throw new JsonException("The PR has no head."),
            body.GetProperty("base").GetProperty("sha").GetString() ?? throw new JsonException("The PR has no base."));
    }

    /// <summary>A file at a commit, or null if it isn't there, which a review reports as an invalid config.</summary>
    public async Task<string?> ReadFileAsync(string path, string commit, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository}/contents/{path}?ref={commit}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public Task<CommentAction> UpsertCommentAsync(int pullRequest, string body, CancellationToken cancellationToken = default) =>
        new StickyComment(http, repository, pullRequest).UpsertAsync(body, cancellationToken);

    /// <summary>Advisory by default (D9): findings are neutral, and only a repo's own rules could make it fail one day.</summary>
    public async Task PostCheckRunAsync(ReviewResult result, CancellationToken cancellationToken = default)
    {
        var (conclusion, title) = result.Verdict switch
        {
            Verdict.NoFindings when result.NotReviewed.Count == 0 => ("success", "No findings"),
            Verdict.NoFindings => ("neutral", "No findings, and some of it wasn't reviewed"),
            Verdict.Findings => ("neutral", result.Findings.Count == 1 ? "1 finding" : $"{result.Findings.Count} findings"),
            _ => ("neutral", "Not reviewed"),
        };

        using var response = await http.PostAsJsonAsync(
            $"repos/{repository}/check-runs",
            new
            {
                name = "tf-review",
                head_sha = result.Commit,
                status = "completed",
                conclusion,
                output = new { title, summary = "The details are in the PR's comment." },
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var response = await http.DeleteAsync("installation/token");
        }
        catch (HttpRequestException)
        {
            // It expires within the hour anyway, and this mustn't replace the review's own outcome.
        }

        http.Dispose();
    }
}
