using System.Net.Http.Headers;
using System.Text.Json;

namespace TfReview.GitHub;

public enum CommentAction
{
    Created,
    Updated,
}

/// <summary>Keeps one comment per PR, found by its hidden marker and rewritten on each push.</summary>
public sealed class StickyComment(HttpClient http, string repository, int pullRequest)
{
    // Anyone can post a comment containing the marker, so only these two count: the App the service posts as, and
    // the workflow's own token, which says "not reviewed" when the service is down (D39). Either replaces the other's,
    // so an older result can't stand for a newer commit.
    private static readonly HashSet<string> BotLogins = ["tf-review-gergodrazsdik[bot]", "github-actions[bot]"];

    private const int PageSize = 100;

    public static HttpClient CreateClient(Uri apiUrl, string token)
    {
        var http = new HttpClient { BaseAddress = apiUrl };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("tf-review");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public Task<CommentAction> UpsertAsync(string body, CancellationToken cancellationToken = default) =>
        this.UpsertAsync(_ => body, cancellationToken);

    /// <summary>Writes the body built from the comment it replaces, or from null when there's none yet (D56).</summary>
    public async Task<CommentAction> UpsertAsync(Func<string?, string> build, CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(cancellationToken);
        var request = new { body = build(existing?.Body) };

        using var response = existing is { Id: var id }
            ? await http.PatchAsJsonAsync($"repos/{repository}/issues/comments/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"repos/{repository}/issues/{pullRequest}/comments", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return existing is null ? CommentAction.Created : CommentAction.Updated;
    }

    private async Task<(long Id, string? Body)?> FindAsync(CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            var url = $"repos/{repository}/issues/{pullRequest}/comments?per_page={PageSize}&page={page}";
            using var comments = await http.GetFromJsonAsync<JsonDocument>(url, cancellationToken)
                ?? throw new JsonException("GitHub returned no comment list.");

            foreach (var comment in comments.RootElement.EnumerateArray())
            {
                var author = comment.GetProperty("user").GetProperty("login").GetString();
                var text = comment.GetProperty("body").GetString();
                if (author is not null && BotLogins.Contains(author) && text?.StartsWith(CommentRenderer.Marker, StringComparison.Ordinal) == true)
                {
                    return (comment.GetProperty("id").GetInt64(), text);
                }
            }

            if (comments.RootElement.GetArrayLength() < PageSize)
            {
                return null;
            }
        }
    }
}
