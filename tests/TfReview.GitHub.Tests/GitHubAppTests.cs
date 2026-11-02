using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TfReview.GitHub.Tests;

public class GitHubAppTests
{
    private const string ClientId = "Iv23test";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly RSA Key = RSA.Create(2048);

    [Test]
    public async Task The_app_proves_itself_with_a_signed_token_github_accepts()
    {
        var parts = App(new FakeGitHub()).CreateJwt().Split('.');

        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        var payload = JsonDocument.Parse(Decode(parts[1])).RootElement;
        await Assert.That(header.GetProperty("alg").GetString()).IsEqualTo("RS256");
        await Assert.That(payload.GetProperty("iss").GetString()).IsEqualTo(ClientId);
        await Assert.That(payload.GetProperty("iat").GetInt64()).IsEqualTo(Now.ToUnixTimeSeconds() - 60);

        // GitHub refuses a token that lasts over ten minutes.
        var lifetime = payload.GetProperty("exp").GetInt64() - Now.ToUnixTimeSeconds();
        await Assert.That(lifetime).IsLessThanOrEqualTo(600);

        var verified = Key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await Assert.That(verified).IsTrue();
    }

    [Test]
    public async Task A_token_covers_one_repo_and_can_never_write_code()
    {
        var github = new FakeGitHub();

        await using var session = await App(github).OpenAsync("owner/repo", new Uri("https://api.example.test/"));

        var request = JsonDocument.Parse(github.MintBody!).RootElement;
        await Assert.That(request.GetProperty("repositories").EnumerateArray().Single().GetString()).IsEqualTo("repo");
        var permissions = request.GetProperty("permissions");
        await Assert.That(permissions.GetProperty("contents").GetString()).IsEqualTo("read");
        await Assert.That(permissions.GetProperty("pull_requests").GetString()).IsEqualTo("write");
        await Assert.That(permissions.GetProperty("checks").GetString()).IsEqualTo("write");
        await Assert.That(permissions.EnumerateObject().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task A_repo_without_the_app_installed_is_an_error()
    {
        var github = new FakeGitHub { Installed = false };

        await Assert.ThrowsAsync<GitHubAppException>(() => App(github).OpenAsync("owner/repo", new Uri("https://api.example.test/")));

        await Assert.That(github.MintBody).IsNull();
    }

    private static GitHubApp App(FakeGitHub github) => new(
        new HttpClient(github) { BaseAddress = new Uri("https://api.example.test/") },
        ClientId,
        Key.ExportRSAPrivateKeyPem(),
        new FixedTime());

    private static byte[] Decode(string part)
    {
        var text = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public bool Installed { get; init; } = true;

        public string? MintBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get)
            {
                return this.Installed
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":177936120}""") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            this.MintBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"token":"test-token"}""") };
        }
    }
}
