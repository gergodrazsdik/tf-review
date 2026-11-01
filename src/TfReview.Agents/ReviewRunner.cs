using TfReview.Core;
using TfReview.Rag;
using TfReview.Scanners;

namespace TfReview.Agents;

/// <summary>
/// One review, from a redacted plan to a result. The plan came from a job that ran PR code, so it's
/// redacted again here: that changes nothing on an honest plan, and refuses anything else. Every failure
/// becomes a result saying what wasn't reviewed, so the PR always gets an answer for this commit.
/// </summary>
public sealed class ReviewRunner(IStandardsSource standards, ConftestRunner conftest)
{
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

            var findings = ran.ToFindings(CondensedPlan.From(redacted.Plan));
            return new ReviewResult(
                1,
                commit,
                findings.Count == 0 ? Verdict.NoFindings : Verdict.Findings,
                [.. findings.OrderByDescending(finding => finding.Severity)],

                // The agent doesn't exist yet, and the comment shouldn't suggest the review was complete.
                [new NotReviewed(ReviewScope.Conventions, NotReviewedReason.Unavailable)],
                new Dictionary<string, int>())
            {
                StandardsCommit = config.Commit,
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
}
