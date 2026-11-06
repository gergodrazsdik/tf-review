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

        // Only the queue is needed before a message is taken. Everything else is set up on first use, after that, so a
        // missing secret, a Key Vault fault or a bad setting fails a try of that message, which ends in "not reviewed"
        // (D39), rather than crashing every execution while the message waits on the queue for a week.
        var stores = ServiceStores.FromEnvironment();
        using var appHttp = StickyComment.CreateClient(GitHubApi, string.Empty);
        appHttp.DefaultRequestHeaders.Authorization = null;
        using var standardsHttp = new HttpClient { BaseAddress = new Uri("https://codeload.github.com/"), Timeout = TimeSpan.FromSeconds(30) };

        // Secrets come from Key Vault into memory, never from the job's definition (D47). Only a success is kept, so
        // the give-up after a failed try asks again.
        SecretClient? secrets = null;
        SecretClient Secrets() => secrets ??= new SecretClient(
            new Uri($"https://{Required("TF_REVIEW_KEY_VAULT")}.vault.azure.net/"), new DefaultAzureCredential());

        GitHubApp? app = null;
        async Task<GitHubApp> AppAsync(CancellationToken cancellationToken) => app ??= new GitHubApp(
            appHttp,
            Required("TF_REVIEW_GITHUB_APP_CLIENT_ID"),
            (await Secrets().GetSecretAsync(AppKeySecret, cancellationToken: cancellationToken)).Value.Value);

        var budget = stores.Counters is { } counters
            ? new DailyBudget(counters, DailyBudget.LimitFrom(Environment.GetEnvironmentVariable(DailyBudget.Variable)))
            : null;

        ReviewRunner? runner = null;
        async Task<ReviewRunner> RunnerAsync(CancellationToken cancellationToken)
        {
            if (runner is not null)
            {
                return runner;
            }

            // One client for the reference index and the result cache, which live in the same account.
            var cosmos = new Microsoft.Azure.Cosmos.CosmosClient(
                Required("TF_REVIEW_COSMOS_ENDPOINT"),
                new DefaultAzureCredential(),
                new Microsoft.Azure.Cosmos.CosmosClientOptions { ApplicationName = "tf-review", ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway });

            // Each call's estimated cost goes to the daily budget (D62), measured on the model, under the cache.
            var model = ReviewModel.FromEnvironment();
            if (model is not null && budget is not null)
            {
                model = model with { Chat = new SpendMeter(model.Chat, model.Deployment, budget.Spend) };
            }

            return runner = new ReviewRunner(
                new GitHubTarballSource(standardsHttp),
                new ConftestRunner(new ProcessRunner()),
                model,
                log: Console.WriteLine,
                infracost: await InfracostAsync(Secrets(), cancellationToken),
                checkov: new CheckovRunner(new ProcessRunner()),
                references: References(cosmos),
                answers: new CosmosAnswerStore(cosmos.GetContainer(Database, "reviews")),
                timeLimits: ReviewTimeLimits.Worker);
        }

        var worker = new ReviewWorker(
            stores.Queue,
            stores.Plans,
            stores.Statuses,
            async (repository, cancellationToken) => await (await AppAsync(cancellationToken)).OpenAsync(repository, GitHubApi, cancellationToken),
            async (plan, config, lockFile, commit, repositoryId, cancellationToken) =>
                await (await RunnerAsync(cancellationToken)).RunAsync(plan, config, commit, cancellationToken, lockFile, repositoryId),
            Console.WriteLine,
            budget: budget);

        return await worker.RunOnceAsync(deadline.Token);
    }

    private static MeterProvider? MetricsProvider() =>
        Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING") is { Length: > 0 } connectionString
            ? Sdk.CreateMeterProviderBuilder()
                .AddMeter(ReviewMetrics.MeterName)
                .AddAzureMonitorMetricExporter(options => options.ConnectionString = connectionString)
                .Build()
            : null;

    // Without its token Infracost can't run, which costs the cost scope only ("not reviewed"), not the whole review.
    // Setting the variable doesn't reach /proc, and only the Infracost runner passes it on.
    private static async Task<InfracostRunner?> InfracostAsync(SecretClient secrets, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            // A newline stored with a token makes it invalid as a header, and Infracost then can't run at all.
            token = (await secrets.GetSecretAsync(InfracostSecret, cancellationToken: cancellationToken)).Value.Value.Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.WriteLine($"The Infracost token couldn't be read, so cost isn't reviewed: {e.GetType().Name}.");
            return null;
        }

        Environment.SetEnvironmentVariable(InfracostVariable, token);
        return new InfracostRunner(new ProcessRunner(InfracostRunner.EnvironmentVariables));
    }

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

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} isn't set.");
}
