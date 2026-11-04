using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Security.KeyVault.Secrets;
using OpenTelemetry;
using OpenTelemetry.Metrics;
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
    private const string Database = "tfreview";

    private static readonly Uri GitHubApi = new("https://api.github.com/");

    public static async Task<int> RunAsync()
    {
        // Nothing is exported by a job that has exited, so this flushes when it's disposed, and an export that fails
        // can't change the review's outcome. Scanners don't get the connection string (D47).
        using var metrics = MetricsProvider();

        // The job's own limit is lower than the message's visibility, and this stops it cleanly before that.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(14));

        // Secrets come from Key Vault into memory, never from the job's definition (D47). Setting the variable
        // doesn't reach /proc, and only the Infracost runner passes it on.
        var secrets = new SecretClient(new Uri($"https://{Required("TF_REVIEW_KEY_VAULT")}.vault.azure.net/"), new DefaultAzureCredential());
        var appKey = (await secrets.GetSecretAsync(AppKeySecret, cancellationToken: deadline.Token)).Value.Value;

        // A newline stored with a token makes it invalid as a header, and Infracost then can't run at all.
        var infracost = (await secrets.GetSecretAsync(InfracostSecret, cancellationToken: deadline.Token)).Value.Value.Trim();
        Environment.SetEnvironmentVariable(InfracostVariable, infracost);

        var stores = ServiceStores.FromEnvironment();
        using var appHttp = StickyComment.CreateClient(GitHubApi, string.Empty);
        appHttp.DefaultRequestHeaders.Authorization = null;
        var app = new GitHubApp(appHttp, Required("TF_REVIEW_GITHUB_APP_CLIENT_ID"), appKey);

        // One client for the reference index and the result cache, which live in the same account.
        var cosmos = new Microsoft.Azure.Cosmos.CosmosClient(
            Required("TF_REVIEW_COSMOS_ENDPOINT"),
            new DefaultAzureCredential(),
            new Microsoft.Azure.Cosmos.CosmosClientOptions { ApplicationName = "tf-review", ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway });

        using var standardsHttp = new HttpClient { BaseAddress = new Uri("https://codeload.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        var runner = new ReviewRunner(
            new GitHubTarballSource(standardsHttp),
            new ConftestRunner(new ProcessRunner()),
            Model(),
            log: Console.WriteLine,
            infracost: new InfracostRunner(new ProcessRunner(InfracostRunner.EnvironmentVariables)),
            checkov: new CheckovRunner(new ProcessRunner()),
            references: References(cosmos),
            answers: new CosmosAnswerStore(cosmos.GetContainer(Database, "reviews")));

        var worker = new ReviewWorker(
            stores.Queue,
            stores.Plans,
            stores.Statuses,
            (repository, cancellationToken) => app.OpenAsync(repository, GitHubApi, cancellationToken),
            (plan, config, lockFile, commit, repositoryId, cancellationToken) =>
                runner.RunAsync(plan, config, commit, cancellationToken, lockFile, repositoryId),
            Console.WriteLine);

        return await worker.RunOnceAsync(deadline.Token);
    }

    private static MeterProvider? MetricsProvider() =>
        Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING") is { Length: > 0 } connectionString
            ? Sdk.CreateMeterProviderBuilder()
                .AddMeter(ReviewMetrics.MeterName)
                .AddAzureMonitorMetricExporter(options => options.ConnectionString = connectionString)
                .Build()
            : null;

    // Read-only use of the index the corpus workflow builds. Only configs asking for references get any (D42).
    private static CosmosReferenceRetriever? References(Microsoft.Azure.Cosmos.CosmosClient cosmos)
    {
        var endpoint = Environment.GetEnvironmentVariable("TF_REVIEW_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("TF_REVIEW_EMBEDDING_DEPLOYMENT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(deployment))
        {
            return null;
        }

        return new CosmosReferenceRetriever(
            cosmos.GetContainer(Database, "references"), AzureQueryEmbedder.Create(uri, deployment, new DefaultAzureCredential()));
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
