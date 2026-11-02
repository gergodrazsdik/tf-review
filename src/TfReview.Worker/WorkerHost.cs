using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using TfReview.Agents;
using TfReview.Core;
using TfReview.GitHub;
using TfReview.Rag;
using TfReview.Scanners;
using TfReview.Service;

namespace TfReview.Worker;

/// <summary>Runs the worker from its environment, as a job execution.</summary>
public static class WorkerHost
{
    private const string AppKeySecret = "github-app-private-key";
    private const string InfracostSecret = "infracost-cli-token";
    private const string InfracostVariable = "INFRACOST_CLI_AUTHENTICATION_TOKEN";

    private static readonly Uri GitHubApi = new("https://api.github.com/");

    public static async Task<int> RunAsync()
    {
        // The job's own limit is lower than the message's visibility, and this stops it cleanly before that.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(14));

        // Secrets come from Key Vault into memory, never from the job's definition (D47). Setting the variable
        // doesn't reach /proc, and only the Infracost runner passes it on.
        var secrets = new SecretClient(new Uri($"https://{Required("TF_REVIEW_KEY_VAULT")}.vault.azure.net/"), new DefaultAzureCredential());
        var appKey = (await secrets.GetSecretAsync(AppKeySecret, cancellationToken: deadline.Token)).Value.Value;
        var infracost = (await secrets.GetSecretAsync(InfracostSecret, cancellationToken: deadline.Token)).Value.Value;
        Environment.SetEnvironmentVariable(InfracostVariable, infracost);

        var stores = ServiceStores.FromEnvironment();
        using var appHttp = StickyComment.CreateClient(GitHubApi, string.Empty);
        appHttp.DefaultRequestHeaders.Authorization = null;
        var app = new GitHubApp(appHttp, Required("TF_REVIEW_GITHUB_APP_CLIENT_ID"), appKey);

        using var standardsHttp = new HttpClient { BaseAddress = new Uri("https://codeload.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        var runner = new ReviewRunner(
            new GitHubTarballSource(standardsHttp),
            new ConftestRunner(new ProcessRunner()),
            Model(),
            log: Console.WriteLine,
            infracost: new InfracostRunner(new ProcessRunner(InfracostRunner.EnvironmentVariables)),
            checkov: new CheckovRunner(new ProcessRunner()));

        var worker = new ReviewWorker(
            stores.Queue,
            stores.Plans,
            stores.Statuses,
            (repository, cancellationToken) => app.OpenAsync(repository, GitHubApi, cancellationToken),
            (plan, config, commit, cancellationToken) => runner.RunAsync(plan, config, commit, cancellationToken),
            Console.WriteLine);

        return await worker.RunOnceAsync(deadline.Token);
    }

    // No model configured means the agents report "not reviewed", rather than the whole review failing.
    private static ReviewModel? Model()
    {
        var endpoint = Environment.GetEnvironmentVariable("TF_REVIEW_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("TF_REVIEW_OPENAI_DEPLOYMENT");
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(deployment)
            ? new ReviewModel(LlmClient.Create(uri, deployment), deployment)
            : null;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} isn't set.");
}
