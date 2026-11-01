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
/// <param name="infracost">Prices the plan, or null when there's no token, which is reported as not reviewed.</param>
/// <param name="checkov">Checks the plan, or null when it isn't available, which is reported as not reviewed.</param>
public sealed class ReviewRunner(
    IStandardsSource standards,
    ConftestRunner conftest,
    ReviewModel? model = null,
    IArbiter? arbiter = null,
    Action<string>? log = null,
    InfracostRunner? infracost = null,
    CheckovRunner? checkov = null)
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

            var (cost, costFailure) = Enabled(config, AgentProfile.Cost)
                ? await RunCostAsync(redacted.Plan, cancellationToken)
                : (null, null);
            var (checkovFindings, checkovFailure) = Enabled(config, AgentProfile.Security)
                ? await RunCheckovAsync(redacted.Plan, plan, cancellationToken)
                : ([], null);

            // A failed scan already says that scope wasn't reviewed, and a cost agent without figures has nothing to judge.
            var passes = await Task.WhenAll(
                Enabled(config, AgentProfile.Conventions)
                    ? RunAgentAsync(AgentProfile.Conventions, plan, policyFindings, corpus, null, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped),
                Enabled(config, AgentProfile.Security)
                    ? RunAgentAsync(AgentProfile.Security, plan, policyFindings, corpus, null, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped),
                cost is { } && cost.Resources.Any(resource => resource.Priced && resource.AfterUsd > resource.BeforeUsd)
                    ? RunAgentAsync(AgentProfile.Cost, plan, [], corpus, cost.Resources, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped));

            // Each agent's findings are checked against the documents in its own prompt, not everyone's.
            var scanners = EvidenceChecker.Check([.. policyFindings, .. checkovFindings], plan, []);
            var checks = passes.Select(pass => EvidenceChecker.Check(pass.Findings, plan, pass.Docs)).Prepend(scanners).ToList();
            var arbitration = await arbiter.ArbitrateAsync([.. checks.SelectMany(check => check.Kept)], plan, cancellationToken);

            IReadOnlyList<NotReviewed> notReviewed =
            [
                .. new[] { checkovFailure, costFailure }.OfType<NotReviewed>()
                    .Concat(passes.SelectMany(pass => pass.NotReviewed))
                    .DistinctBy(missing => missing.Scope)
                    .OrderBy(missing => missing.Scope),
            ];
            var omitted = passes.SelectMany(pass => pass.Omitted).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            return new ReviewResult(1, commit, arbitration.Verdict, arbitration.Findings, notReviewed, MergeDrops(checks))
            {
                Cost = cost,
                StandardsCommit = config.Commit,
                StandardsRepository = config.Repository,
                DocPaths = DocPaths(arbitration.Findings, corpus),
                OmittedDocs = omitted.Count == 0 ? null : omitted,
                Usage = Usage([.. passes.SelectMany(pass => pass.Calls)]),
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

    private static bool Enabled(TfReviewConfig config, AgentProfile profile) => config.Agents.Contains(profile.Name, StringComparer.Ordinal);

    private static Dictionary<string, int> MergeDrops(IEnumerable<EvidenceCheck> checks)
    {
        var drops = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (reason, count) in checks.SelectMany(check => check.Drops))
        {
            drops[reason] = drops.GetValueOrDefault(reason) + count;
        }

        return drops;
    }

    /// <summary>A broken corpus stops the agents, but the policies have already run and their findings still count.</summary>
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

    // A failed scan isn't a clean one, and it doesn't undo the rest of the review.
    private async Task<(IReadOnlyList<Finding> Findings, NotReviewed? Failure)> RunCheckovAsync(
        RedactedPlan redacted, CondensedPlan plan, CancellationToken cancellationToken)
    {
        if (checkov is null)
        {
            return ([], new NotReviewed(ReviewScope.Security, NotReviewedReason.Unavailable));
        }

        return await checkov.RunAsync(redacted, cancellationToken) is CheckovRan ran
            ? (ran.ToFindings(plan), null)
            : ([], new NotReviewed(ReviewScope.Security, NotReviewedReason.ScannerFailed));
    }

    // A cost failure stays separate: the policies and the conventions agent still count without it.
    private async Task<(CostSummary? Cost, NotReviewed? Failure)> RunCostAsync(RedactedPlan plan, CancellationToken cancellationToken)
    {
        if (infracost is null)
        {
            return (null, new NotReviewed(ReviewScope.Cost, NotReviewedReason.Unavailable));
        }

        if (await infracost.RunAsync(plan, cancellationToken) is not InfracostRan ran)
        {
            return (null, new NotReviewed(ReviewScope.Cost, NotReviewedReason.ScannerFailed));
        }

        var priced = ran.Costs.Where(cost => cost.Priced).ToList();
        return (
            new CostSummary(
                priced.Sum(cost => cost.Before),
                priced.Sum(cost => cost.After),
                [.. ran.Costs.Select(cost => new ResourceCostChange(cost.Address, cost.Before, cost.After, cost.Priced))]),
            null);
    }

    private async Task<AgentPass> RunAgentAsync(
        AgentProfile profile,
        CondensedPlan plan,
        IReadOnlyList<Finding> policyFindings,
        StandardsCorpus? corpus,
        IReadOnlyList<ResourceCostChange>? costs,
        CancellationToken cancellationToken)
    {
        if (corpus is null)
        {
            return AgentPass.NotReviewedFor(profile, NotReviewedReason.StandardsUnavailable);
        }

        if (model is null)
        {
            return AgentPass.NotReviewedFor(profile, NotReviewedReason.Unavailable);
        }

        var retrieval = await new FileRetriever(corpus).RetrieveAsync(
            new RetrievalQuery(profile.Topics, plan.ResourceTypes()), cancellationToken);

        var outcome = await new ReviewAgent(model.Chat, profile).ReviewAsync(plan, retrieval.Docs, policyFindings, costs, cancellationToken);
        foreach (var call in outcome.Calls)
        {
            log?.Invoke(
                $"{profile.Name}: prompt v{outcome.PromptVersion}, model {model.Deployment}, attempt {call.Attempt}, "
                + $"finish {call.Finish}, {call.InputTokens} in ({call.CachedInputTokens} cached), {call.OutputTokens} out, "
                + $"{(long)call.Latency.TotalMilliseconds} ms.");
        }

        return new AgentPass(
            outcome is AgentReviewed reviewed ? reviewed.Findings : [],
            retrieval.Docs,
            retrieval.OmittedDocIds,
            outcome is AgentFailed failed ? [new NotReviewed(profile.Scope, failed.Reason)] : [],
            outcome.Calls);
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

    private sealed record AgentPass(
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<StandardsDoc> Docs,
        IReadOnlyList<string> Omitted,
        IReadOnlyList<NotReviewed> NotReviewed,
        IReadOnlyList<AgentCall> Calls)
    {
        public static readonly AgentPass Skipped = new([], [], [], [], []);

        public static AgentPass NotReviewedFor(AgentProfile profile, NotReviewedReason reason) =>
            new([], [], [], [new NotReviewed(profile.Scope, reason)], []);
    }
}
