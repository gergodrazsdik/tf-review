using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents;

/// <summary>The model the agents call, and the deployment name reported in the usage.</summary>
public sealed record ReviewModel(IChatClient Chat, string Deployment);

/// <summary>
/// Where reference material goes. Per finding is the default (D50): per agent stays for the evals, which compare the two.
/// </summary>
public enum ReferencePlacement
{
    /// <summary>Into each agent's prompt, searched for its slice (D42).</summary>
    Agent,

    /// <summary>Searched for each finding after the review, and only ever added to its suggestion (D50).</summary>
    Finding,
}

/// <summary>
/// One review, from a redacted plan to a result. The plan came from a job that ran PR code, so it's
/// redacted again here: that changes nothing on an honest plan, and refuses anything else. Every failure
/// becomes a result saying what wasn't reviewed, so the PR always gets an answer for this commit.
/// </summary>
/// <param name="standards">Where the corpus comes from.</param>
/// <param name="conftest">Runs the corpus's policies.</param>
/// <param name="model">The model, or null when none is configured, which is reported as not reviewed.</param>
/// <param name="arbiter">Combines the findings. An LLM arbiter within code's limits if there's a model, or else rule-based.</param>
/// <param name="log">Receives one line per model call, holding no prompt or answer text.</param>
/// <param name="infracost">Prices the plan, or null when there's no token, which is reported as not reviewed.</param>
/// <param name="checkov">Checks the plan, or null when it isn't available, which is reported as not reviewed.</param>
/// <param name="references">Imported reference material, for a config that asks for it, or null when there's none (D42).</param>
/// <param name="placement">Where references go when a config asks for them.</param>
/// <param name="answers">Keeps model answers between reviews of the same repo, or null for none (D53).</param>
public sealed class ReviewRunner(
    IStandardsSource standards,
    ConftestRunner conftest,
    ReviewModel? model = null,
    IArbiter? arbiter = null,
    Action<string>? log = null,
    InfracostRunner? infracost = null,
    CheckovRunner? checkov = null,
    IReferenceRetriever? references = null,
    ReferencePlacement placement = ReferencePlacement.Finding,
    IAnswerStore? answers = null)
{
    /// <summary>
    /// Each agent's share for references, apart from its standards' cap, so they can't push a rule out (D42). About
    /// three chunks, which keeps a review's cost within about double what it was without them.
    /// </summary>
    public const int ReferenceTokenBudget = 2_000;

    private static readonly string[] CommonAttributes = ["location", "resource_group_name"];

    /// <summary>The evidence_drops key for resources a model listed while saying they don't break a standard.</summary>
    public const string ModelRetracted = "model_retracted";

    private readonly IArbiter? customArbiter = arbiter;

    /// <param name="redactedPlan">The plan, which is redacted again here.</param>
    /// <param name="configYaml">The config at the base commit.</param>
    /// <param name="commit">The commit reviewed.</param>
    /// <param name="cancellationToken">Stops the review.</param>
    /// <param name="lockFile">The project's lock file at the base commit, which says which provider docs apply.</param>
    /// <param name="repositoryId">The repo's ID, which scopes the result cache. Without it nothing is cached.</param>
    public async Task<ReviewResult> RunAsync(
        string redactedPlan,
        string? configYaml,
        string commit,
        CancellationToken cancellationToken = default,
        string? lockFile = null,
        string? repositoryId = null)
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
                log?.Invoke($"conftest: {(outcome as ConftestFailed)?.Reason}.");
                return ReviewResult.Failed(commit, ReviewScope.Plan, NotReviewedReason.ScannerFailed) with
                {
                    StandardsCommit = config.Commit,
                };
            }

            // Every model role goes through this, so each can reuse an answer to exactly the same input (D53).
            var chat = model is null ? null
                : answers is not null && repositoryId is not null ? new AnswerCache(model.Chat, answers, repositoryId, model.Deployment, log)
                : model.Chat;
            var arbitrator = customArbiter ?? (chat is null ? new RuleArbiter() : new LlmArbiter(chat));

            var plan = CondensedPlan.From(redacted.Plan);
            var policyFindings = ran.ToFindings(plan);
            var corpus = await LoadCorpusAsync(directory.FullName, cancellationToken);

            var (cost, costFailure) = Enabled(config, AgentProfile.Cost)
                ? await RunCostAsync(redacted.Plan, cancellationToken)
                : (null, null);
            var (checkovFindings, checkovFailure) = Enabled(config, AgentProfile.Security)
                ? await RunCheckovAsync(redacted.Plan, plan, cancellationToken)
                : ([], null);

            // It decides only an order, so it can start alongside the agents and fail without costing them anything.
            var ranking = chat is null || plan.Changes().Count == 0
                ? Task.FromResult(new RiskRanking(RiskOrchestrator.ByRules(plan), [], null))
                : new RiskOrchestrator(chat).RankAsync(plan, cancellationToken);

            var referenceQuery = config.References && references is not null ? new ReferenceContext(LockFile.AzurermVersion(lockFile)) : null;
            var agentReferences = placement == ReferencePlacement.Agent ? referenceQuery : null;

            // A failed scan already says that scope wasn't reviewed, and a cost agent without figures has nothing to judge.
            var passes = await Task.WhenAll(
                Enabled(config, AgentProfile.Conventions)
                    ? RunAgentAsync(chat, AgentProfile.Conventions, plan, policyFindings, corpus, null, agentReferences, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped),
                Enabled(config, AgentProfile.Security)
                    ? RunAgentAsync(chat, AgentProfile.Security, plan, policyFindings, corpus, null, agentReferences, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped),
                cost is { } && cost.Resources.Any(resource => resource.Priced && resource.AfterUsd > resource.BeforeUsd)
                    ? RunAgentAsync(chat, AgentProfile.Cost, plan, [], corpus, cost.Resources, agentReferences, cancellationToken)
                    : Task.FromResult(AgentPass.Skipped));

            // Each agent's findings are checked against the documents in its own prompt, not everyone's.
            var scanners = EvidenceChecker.Check([.. policyFindings, .. checkovFindings], plan, []);
            var checks = passes.Select(pass => EvidenceChecker.Check(pass.Findings, pass.Plan ?? plan, pass.Docs)).Prepend(scanners).ToList();

            var risk = await ranking;
            Log(RiskOrchestrator.Name, risk.PromptVersion, risk.Calls);
            var arbitration = await arbitrator.ArbitrateAsync(
                [.. checks.SelectMany(check => check.Kept)], plan, [.. risk.Order.Select(item => item.ResourceAddress)], cancellationToken);
            Log(LlmArbiter.Name, arbitration.PromptVersion, arbitration.Calls);

            // After the arbiter, so each problem is searched once, and before the result, which the comment renders.
            var suggestions = placement == ReferencePlacement.Finding && referenceQuery is not null && chat is not null && corpus is not null
                ? await new ReferenceSuggester(references!, chat)
                    .SuggestAsync(arbitration.Findings, plan, corpus.Docs, referenceQuery.ProviderVersion, cancellationToken)
                : null;
            if (suggestions is not null)
            {
                Log(ReferenceSuggester.Name, suggestions.PromptVersion, suggestions.Calls);
            }

            var findings = suggestions?.Findings ?? arbitration.Findings;

            IReadOnlyList<NotReviewed> notReviewed =
            [
                .. new[] { checkovFailure, costFailure }.OfType<NotReviewed>()
                    .Concat(passes.SelectMany(pass => pass.NotReviewed))
                    .DistinctBy(missing => missing.Scope)
                    .OrderBy(missing => missing.Scope),
            ];
            var omitted = passes.SelectMany(pass => pass.Omitted).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            var drops = MergeDrops(checks);
            var retracted = passes.Sum(pass => pass.Retracted);
            if (retracted > 0)
            {
                drops[ModelRetracted] = retracted;
            }

            return new ReviewResult(1, commit, arbitration.Verdict, findings, notReviewed, drops)
            {
                RiskOrder = risk.Order.Count == 0 ? null : risk.Order,
                Cost = cost,
                StandardsCommit = config.Commit,
                StandardsRepository = config.Repository,
                DocPaths = DocPaths(findings, corpus),
                OmittedDocs = omitted.Count == 0 ? null : omitted,
                Usage = Usage([.. passes.SelectMany(pass => pass.Calls), .. risk.Calls, .. arbitration.Calls, .. suggestions?.Calls ?? []]),
                References = referenceQuery is null ? null : ReferenceUseOf([.. passes.Select(pass => pass.References), .. suggestions?.Retrievals ?? []]),
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

        var outcome = await checkov.RunAsync(redacted, cancellationToken);
        if (outcome is CheckovRan ran)
        {
            return (ran.ToFindings(plan), null);
        }

        log?.Invoke($"checkov: {(outcome as CheckovFailed)?.Reason}.");
        return ([], new NotReviewed(ReviewScope.Security, NotReviewedReason.ScannerFailed));
    }

    // A cost failure stays separate: the policies and the conventions agent still count without it.
    private async Task<(CostSummary? Cost, NotReviewed? Failure)> RunCostAsync(RedactedPlan plan, CancellationToken cancellationToken)
    {
        if (infracost is null)
        {
            return (null, new NotReviewed(ReviewScope.Cost, NotReviewedReason.Unavailable));
        }

        var outcome = await infracost.RunAsync(plan, cancellationToken);
        if (outcome is not InfracostRan ran)
        {
            log?.Invoke($"infracost: {(outcome as InfracostFailed)?.Reason}.");
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
        IChatClient? chat,
        AgentProfile profile,
        CondensedPlan plan,
        IReadOnlyList<Finding> policyFindings,
        StandardsCorpus? corpus,
        IReadOnlyList<ResourceCostChange>? costs,
        ReferenceContext? referenceContext,
        CancellationToken cancellationToken)
    {
        if (corpus is null)
        {
            return AgentPass.NotReviewedFor(profile, NotReviewedReason.StandardsUnavailable);
        }

        if (chat is null)
        {
            return AgentPass.NotReviewedFor(profile, NotReviewedReason.Unavailable);
        }

        var retrieval = await new FileRetriever(corpus).RetrieveAsync(
            new RetrievalQuery(profile.Topics, plan.ResourceTypes()), cancellationToken);

        // Code routes (D35): an agent sees the resources its standards cover, so coverage can't depend on a model.
        var slice = plan.OnlyTypes(type => retrieval.Docs.Any(doc => doc.AppliesToType(type)));

        // Only when an agent has standards to judge by, because without them it isn't called at all.
        var (referenceDocs, referenceCalls) = retrieval.Docs.Count > 0 && referenceContext is not null
            ? await this.RetrieveReferencesAsync(chat, profile, slice, referenceContext, cancellationToken)
            : (null, []);

        var outcome = await new ReviewAgent(chat, profile).ReviewAsync(
            slice, retrieval.Docs, policyFindings, costs, cancellationToken, referenceDocs?.Docs);
        Log(profile.Name, outcome.PromptVersion, outcome.Calls);

        return new AgentPass(
            outcome is AgentReviewed reviewed ? reviewed.Findings : [],
            retrieval.Docs,
            retrieval.OmittedDocIds,
            outcome switch
            {
                AgentFailed failed => [new NotReviewed(profile.Scope, failed.Reason)],
                AgentReviewed { SkippedBecause: { } reason, Skipped: var skipped } => [new NotReviewed(profile.Scope, reason) { Resources = skipped }],
                _ => [],
            },
            [.. referenceCalls, .. outcome.Calls])
        {
            Plan = slice,
            Retracted = outcome is AgentReviewed { Retracted: var count } ? count : 0,
            References = referenceDocs,
        };
    }

    /// <summary>
    /// What an agent searches for: its topics, its slice's types and the names of the attributes that change, never their
    /// values. Public so the retrieval evals measure the same query reviews send.
    /// </summary>
    public static ReferenceQuery ReferenceQueryFor(
        AgentProfile profile, CondensedPlan slice, string? providerVersion, ReferenceRanking ranking = ReferenceRanking.Hybrid)
    {
        var types = slice.ResourceTypes();

        // Nearly every resource sets these, so they only blur what a change is about.
        var attributes = slice.ChangedAttributeNames().Where(name => !CommonAttributes.Contains(name, StringComparer.Ordinal));
        return new ReferenceQuery(
            $"{string.Join(", ", profile.Topics)}. Resource types: {string.Join(", ", types)}. Changed attributes: {string.Join(", ", attributes)}.",
            types,
            providerVersion,
            ReferenceTokenBudget,
            ranking);
    }

    // A failed search costs the agent its references, not its review: they can't create a finding anyway. With a model,
    // it re-ranks the search's candidates (D49).
    private async Task<(ReferenceRetrieval Retrieval, IReadOnlyList<AgentCall> Calls)> RetrieveReferencesAsync(
        IChatClient chat,
        AgentProfile profile, CondensedPlan slice, ReferenceContext context, CancellationToken cancellationToken)
    {
        try
        {
            var selection = await new ReferenceSelector(references!, chat)
                .SelectAsync(ReferenceQueryFor(profile, slice, context.ProviderVersion), profile.Topics, slice, cancellationToken);
            Log(ReferenceSelector.Name, selection.PromptVersion, selection.Calls);
            return (selection.Retrieval, selection.Calls);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"{profile.Name}: references unavailable, {e.GetType().Name}.");
            return (ReferenceRetrieval.None, []);
        }
    }

    private static ReferenceUse ReferenceUseOf(IEnumerable<ReferenceRetrieval?> retrievals)
    {
        var used = retrievals.OfType<ReferenceRetrieval>().ToList();
        return new ReferenceUse(
            used.Select(retrieval => retrieval.CorpusVersion).OfType<string>().FirstOrDefault(),
            used.Any(retrieval => retrieval.ProviderDocsMissing),
            [.. used.SelectMany(retrieval => retrieval.Docs).Select(doc => doc.Attribution).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private sealed record ReferenceContext(string? ProviderVersion);

    private void Log(string name, int? promptVersion, IReadOnlyList<AgentCall> calls)
    {
        foreach (var call in calls)
        {
            ReviewMetrics.RecordCall(
                name, promptVersion, model?.Deployment, call.Finish, call.InputTokens, call.CachedInputTokens, call.OutputTokens, call.Latency);
            log?.Invoke(
                $"{name}: prompt v{promptVersion}, model {model?.Deployment}, attempt {call.Attempt}, "
                + $"finish {call.Finish}, {call.InputTokens} in ({call.CachedInputTokens} cached), {call.OutputTokens} out, "
                + $"{(long)call.Latency.TotalMilliseconds} ms.");
        }
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
        /// <summary>What this agent was shown, which its findings have to point into.</summary>
        public CondensedPlan? Plan { get; init; }

        /// <summary>Resources it listed while saying they don't break the standard.</summary>
        public int Retracted { get; init; }

        /// <summary>The reference material it was shown, if the config asked for any.</summary>
        public ReferenceRetrieval? References { get; init; }

        public static readonly AgentPass Skipped = new([], [], [], [], []);

        public static AgentPass NotReviewedFor(AgentProfile profile, NotReviewedReason reason) =>
            new([], [], [], [new NotReviewed(profile.Scope, reason)], []);
    }
}
