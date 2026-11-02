using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TfReview.Service;

/// <summary>Gets the running job's OIDC token from GitHub, for one audience.</summary>
public static class ActionsIdToken
{
    public static async Task<string> GetAsync(
        HttpClient http, string requestUrl, string requestToken, string audience, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{requestUrl}&audience={Uri.EscapeDataString(audience)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("value").GetString() ?? throw new JsonException("GitHub returned no token.");
    }
}

/// <summary>
/// Sends a plan to the review API from the workflow's review job. Nothing in the service runs between PRs, so the first
/// call can meet a cold start, and it's retried until the time runs out. A refusal won't change, so that stops at once.
/// </summary>
public sealed class ReviewSubmitter(
    HttpClient api,
    Func<CancellationToken, Task<string>> token,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    public const string Audience = "tf-review-api";

    private static readonly TimeSpan[] Waits =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>Whether the API accepted the review. Why it didn't is logged, with nothing from the plan.</summary>
    public async Task<bool> SubmitAsync(string plan, string commit, string configPath, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var outcome = await this.TryAsync(plan, commit, configPath, cancellationToken);
            if (outcome is Outcome.Accepted or Outcome.Refused)
            {
                return outcome == Outcome.Accepted;
            }

            if (attempt >= Waits.Length)
            {
                log("The review API didn't answer in time.");
                return false;
            }

            await delay(Waits[attempt], cancellationToken);
        }
    }

    private async Task<Outcome> TryAsync(string plan, string commit, string configPath, CancellationToken cancellationToken)
    {
        try
        {
            // New each time, because these only last minutes, and a cold start's retries can outlast one.
            using var request = new HttpRequestMessage(HttpMethod.Post, "reviews")
            {
                Content = JsonContent.Create(new SubmitBody(commit, configPath, plan)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken));

            using var response = await api.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                log($"Submitted review {body.GetProperty("id").GetString()}.");
                return Outcome.Accepted;
            }

            log($"The review API answered {(int)response.StatusCode}: {await ReasonAsync(response, cancellationToken)}");
            return (int)response.StatusCode >= 500 ? Outcome.TryAgain : Outcome.Refused;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            log($"Couldn't reach the review API: {e.GetType().Name}.");
            return Outcome.TryAgain;
        }
    }

    // A refusal names the rule that failed, which is what someone setting up a repo needs. It can't hold plan text.
    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("refused", out var refused) ? refused.ToString() : "no reason given.";
        }
        catch (JsonException)
        {
            return "no reason given.";
        }
    }

    private enum Outcome
    {
        Accepted,
        Refused,
        TryAgain,
    }

    private sealed record SubmitBody(
        [property: JsonPropertyName("commit")] string Commit,
        [property: JsonPropertyName("config_path")] string ConfigPath,
        [property: JsonPropertyName("plan")] string Plan);
}
