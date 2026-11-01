using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents;

/// <summary>The model the agents call, and the deployment name reported in the usage.</summary>
public sealed record ReviewModel(IChatClient Chat, string Deployment);

/// <summary>
/// One review, from a redacted plan to a result. The plan came from a job that ran PR code, so it's
/// redacted again here: that changes nothing on an honest plan, and refuses anything else. Every failure
/// becomes a result saying what wasn't reviewed, so the PR always gets an answer for this commit.
/// </summary>
/// <param name="standards">Where the corpus comes from.</param>
/// <param name="conftest">Runs the corpus's policies.</param>
/// <param name="model">The model, or null when none is configured, which is reported as not reviewed.</param>
/// <param name="arbiter">Combines the findings. Rule-based unless given another.</param>
/// <param name="log">Receives one line per model call, holding no prompt or answer text.</param>
public sealed class ReviewRunner(
    IStandardsSource standards,
    ConftestRunner conftest,
    ReviewModel? model = null,
    IArbiter? arbiter = null,
    Action<string>? log = null)
{
    private readonly IArbiter arbiter = arbiter ?? new RuleArbiter();

    public async Task<ReviewResult> RunAsync(
        string redactedPlan, string? configYaml, string commit, CancellationToken cancellationToken = default)
    {
        if (new PlanRedactor().Redact(redactedPlan) is not Redacted redacted)
        {
            return ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.RedactionRefused);
        }

        TfReviewConfig config;
        try
        {
            config = TfReviewConfig.Parse(configYaml ?? string.Empty);
        }
        catch (ConfigException)
        {
            return ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.InvalidConfig);
        }

        var directory = Directory.CreateTempSubdirectory("tfreview-standards-");
        try
        {
            await standards.FetchAsync(config, directory.FullName, cancellationToken);
            var outcome = await conftest.RunAsync(redacted.Plan, directory.FullName, cancellationToken);
            if (outcome is not ConftestRan ran)
            {
                return ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.ScannerFailed) with
                {
                    StandardsCommit = config.Commit,
                };
            }

            var plan = CondensedPlan.From(redacted.Plan);
            var policyFindings = ran.ToFindings(plan);
            var corpus = await LoadCorpusAsync(directory.FullName, cancellationToken);
            var conventions = config.Agents.Contains(ConventionsAgent.Name, StringComparer.Ordinal)
                ? await RunConventionsAsync(plan, policyFindings, corpus, cancellationToken)
                : ConventionsPass.Skipped;

            var check = EvidenceChecker.Check([.. policyFindings, .. conventions.Findings], plan, conventions.Docs);
            var arbitration = await arbiter.ArbitrateAsync(check.Kept, plan, cancellationToken);

            return new ReviewResult(1, commit, arbitration.Verdict, arbitration.Findings, conventions.NotReviewed, check.Drops)
            {
                StandardsCommit = config.Commit,
                StandardsRepository = config.Repository,
                DocPaths = DocPaths(arbitration.Findings, corpus),
                OmittedDocs = conventions.Omitted.Count == 0 ? null : conventions.Omitted,
                Usage = conventions.Usage,
            };
        }
        catch (StandardsFetchException)
        {
            return ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.StandardsUnavailable);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A broken corpus stops the agent, but the policies have already run and their findings still count.</summary>
    private static async Task<StandardsCorpus?> LoadCorpusAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await StandardsCorpus.LoadAsync(directory, cancellationToken);
        }
        catch (CorpusException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? DocPaths(IReadOnlyList<Finding> findings, StandardsCorpus? corpus)
    {
        var paths = findings
            .Select(finding => finding.Rule)
            .Distinct(StringComparer.Ordinal)
            .Select(rule => corpus?.Docs.FirstOrDefault(doc => doc.Id == rule))
            .OfType<StandardsDoc>()
            .ToDictionary(doc => doc.Id, doc => doc.Path, StringComparer.Ordinal);

        return paths.Count == 0 ? null : paths;
    }

    private async Task<ConventionsPass> RunConventionsAsync(
        CondensedPlan plan, IReadOnlyList<Finding> policyFindings, StandardsCorpus? corpus, CancellationToken cancellationToken)
    {
        if (corpus is null)
        {
            return ConventionsPass.NotReviewedFor(NotReviewedReason.StandardsUnavailable);
        }

        if (model is null)
        {
            return ConventionsPass.NotReviewedFor(NotReviewedReason.Unavailable);
        }

        var retrieval = await new FileRetriever(corpus).RetrieveAsync(
            new RetrievalQuery(ConventionsAgent.Topics, plan.ResourceTypes()), cancellationToken);

        var outcome = await new ConventionsAgent(model.Chat).ReviewAsync(plan, retrieval.Docs, policyFindings, cancellationToken);
        foreach (var call in outcome.Calls)
        {
            log?.Invoke(
                $"{ConventionsAgent.Name}: prompt v{outcome.PromptVersion}, model {model.Deployment}, attempt {call.Attempt}, "
                + $"finish {call.Finish}, {call.InputTokens} in ({call.CachedInputTokens} cached), {call.OutputTokens} out, "
                + $"{(long)call.Latency.TotalMilliseconds} ms.");
        }

        return new ConventionsPass(
            outcome is AgentReviewed reviewed ? reviewed.Findings : [],
            retrieval.Docs,
            retrieval.OmittedDocIds,
            outcome is AgentFailed failed ? [new NotReviewed(ReviewScope.Conventions, failed.Reason)] : [],
            Usage(outcome.Calls));
    }

    private TokenUsage? Usage(IReadOnlyList<AgentCall> calls) => calls.Count == 0
        ? null
        : new TokenUsage(calls.Sum(call => call.InputTokens), calls.Sum(call => call.OutputTokens))
        {
            CachedInputTokens = calls.Sum(call => call.CachedInputTokens),
            Model = model?.Deployment,
            Calls = calls.Count,
            LatencyMs = (long)calls.Sum(call => call.Latency.TotalMilliseconds),
        };

    private sealed record ConventionsPass(
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<StandardsDoc> Docs,
        IReadOnlyList<string> Omitted,
        IReadOnlyList<NotReviewed> NotReviewed,
        TokenUsage? Usage)
    {
        public static readonly ConventionsPass Skipped = new([], [], [], [], null);

        public static ConventionsPass NotReviewedFor(NotReviewedReason reason) =>
            new([], [], [], [new NotReviewed(ReviewScope.Conventions, reason)], null);
    }
}
