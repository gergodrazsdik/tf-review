using TfReview.Core;

namespace TfReview.Agents;

/// <summary>The findings to report, ranked, and the verdict they lead to.</summary>
public sealed record Arbitration(Verdict Verdict, IReadOnlyList<Finding> Findings);

/// <summary>Combines the findings that passed the evidence check. M2 adds an LLM arbiter behind this (D20).</summary>
public interface IArbiter
{
    Task<Arbitration> ArbitrateAsync(IReadOnlyList<Finding> findings, CondensedPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// Merges duplicates by resource and rule, then ranks by severity. When a scanner and a model report the
/// same thing, the finding stays deterministic and keeps the scanner's title, so plan text steering the
/// model can't rewrite a policy result. The model's suggestion is kept, and it's named as the reporter.
/// </summary>
public sealed class RuleArbiter : IArbiter
{
    public Task<Arbitration> ArbitrateAsync(IReadOnlyList<Finding> findings, CondensedPlan plan, CancellationToken cancellationToken = default)
    {
        Finding[] merged =
        [
            .. findings
                .GroupBy(finding => (finding.ResourceAddress, finding.Rule))
                .Select(group => Merge([.. group]))
                .OrderByDescending(finding => finding.Severity)
                .ThenBy(finding => finding.ResourceAddress, StringComparer.Ordinal)
                .ThenBy(finding => finding.Rule, StringComparer.Ordinal),
        ];

        return Task.FromResult(new Arbitration(merged.Length == 0 ? Verdict.NoFindings : Verdict.Findings, merged));
    }

    private static Finding Merge(IReadOnlyList<Finding> duplicates)
    {
        // Sorted so the bytes don't depend on the order the findings arrived in.
        var ordered = duplicates
            .OrderByDescending(finding => finding.Deterministic)
            .ThenBy(finding => finding.Title, StringComparer.Ordinal)
            .ToList();
        var first = ordered[0];

        return first with
        {
            Severity = ordered.Max(finding => finding.Severity),
            Agent = ordered.FirstOrDefault(finding => finding.Agent != Reporter.Scanner)?.Agent ?? Reporter.Scanner,
            Deterministic = ordered.Any(finding => finding.Deterministic),
            Evidence = MergeEvidence(ordered),
            Suggestion = ordered.Select(finding => finding.Suggestion).FirstOrDefault(suggestion => suggestion is not null),
            MonthlyCostDeltaUsd = ordered.Select(finding => finding.MonthlyCostDeltaUsd).FirstOrDefault(delta => delta is not null),
        };
    }

    private static Evidence[] MergeEvidence(IReadOnlyList<Finding> findings)
    {
        var items = findings.SelectMany(finding => finding.Evidence).Select(item => item.Value).ToList();
        return
        [
            .. items.OfType<PlanEvidence>().Distinct().OrderBy(item => item.PlanPointer, StringComparer.Ordinal).Select(item => (Evidence)item),
            .. items.OfType<DocEvidence>().Distinct().OrderBy(item => item.DocId, StringComparer.Ordinal).Select(item => (Evidence)item),
        ];
    }
}
