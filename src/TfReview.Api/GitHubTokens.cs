using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TfReview.Api;

/// <summary>Checks a GitHub Actions OIDC token's signature, issuer, audience and lifetime, and reads its claims.</summary>
public sealed class GitHubTokens(TokenValidationParameters parameters)
{
    public const string Issuer = "https://token.actions.githubusercontent.com";

    private static readonly string[] ClaimNames =
    [
        "event_name", "environment", "repository_id", "repository", "job_workflow_ref", "ref", "run_id", "run_attempt",
    ];

    private readonly JsonWebTokenHandler handler = new();

    /// <summary>The checks for real tokens, with GitHub's published keys, which are refreshed when they rotate.</summary>
    public static GitHubTokens ForGitHub(string audience) => new(Parameters(
        audience,
        Issuer,
        parameters => parameters.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{Issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true })));

    public static TokenValidationParameters Parameters(string audience, string issuer, Action<TokenValidationParameters> keys)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateIssuer = true,
            ValidAudience = audience,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        keys(parameters);
        return parameters;
    }

    /// <summary>The token's claims, or null if it isn't valid. Why it wasn't is never told to the caller.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> ValidateAsync(string token)
    {
        var result = await this.handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ClaimNames)
        {
            if (jwt.TryGetPayloadValue<string>(name, out var value))
            {
                claims[name] = value;
            }
        }

        return claims;
    }
}
