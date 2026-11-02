using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TfReview.GitHub;

public sealed class GitHubAppException(string message) : Exception(message);

/// <summary>
/// The service's identity on GitHub. Each review gets a token limited to one repo and only the rights it needs, so a
/// review of one repo can't reach another, and no token can write code.
/// </summary>
public sealed class GitHubApp(HttpClient http, string clientId, string privateKeyPem, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Opens a repo for one review. The token is revoked when it's disposed.</summary>
    public async Task<RepositorySession> OpenAsync(string repository, Uri apiUrl, CancellationToken cancellationToken = default)
    {
        var name = repository.Split('/') is [_, var repo] ? repo : throw new GitHubAppException("The repository isn't owner/name.");
        var jwt = this.CreateJwt();

        using var lookup = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository}/installation");
        lookup.Headers.Authorization = new("Bearer", jwt);
        using var found = await http.SendAsync(lookup, cancellationToken);
        if (!found.IsSuccessStatusCode)
        {
            // Installing the App is a separate step from allowing a repo (D40).
            throw new GitHubAppException($"Looking up the App's installation returned {(int)found.StatusCode}.");
        }

        var installation = await found.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        using var mint = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installation.GetProperty("id").GetInt64()}/access_tokens")
        {
            Content = JsonContent.Create(new
            {
                repositories = new[] { name },
                permissions = new { contents = "read", pull_requests = "write", checks = "write" },
            }),
        };
        mint.Headers.Authorization = new("Bearer", jwt);
        using var minted = await http.SendAsync(mint, cancellationToken);
        if (!minted.IsSuccessStatusCode)
        {
            throw new GitHubAppException($"Getting a token returned {(int)minted.StatusCode}.");
        }

        var token = (await minted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("token").GetString()
            ?? throw new GitHubAppException("GitHub returned no token.");
        return new RepositorySession(StickyComment.CreateClient(apiUrl, token), repository);
    }

    /// <summary>The App's own short-lived proof of identity, which can only be used to get a token.</summary>
    internal string CreateJwt()
    {
        var now = this.time.GetUtcNow().ToUnixTimeSeconds();

        // A minute back for clock drift, and GitHub refuses anything over ten minutes ahead.
        var header = Encode("""{"alg":"RS256","typ":"JWT"}"""u8);
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = clientId }));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Encode(signature)}";
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
