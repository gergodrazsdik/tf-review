using TfReview.Core;

namespace TfReview.GitHub.Tests;

public class CommentRendererTests
{
    private static readonly string Commit = new('a', 40);

    private static readonly PlanCounts Counts = new(
        3, new Dictionary<string, int> { ["create"] = 2, ["delete"] = 1, ["update"] = 0 });

    [Test]
    public async Task Starts_with_the_marker_the_sticky_comment_looks_for()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), Counts);

        await Assert.That(comment).StartsWith(CommentRenderer.Marker);
    }

    [Test]
    public async Task Says_when_nothing_was_reviewed_and_why()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), Counts);

        await Assert.That(comment).Contains("**Not reviewed** for commit `aaaaaaa`");
        await Assert.That(comment).Contains("Not reviewed: conventions (unavailable).");
    }

    [Test]
    public async Task Summarizes_the_plan_without_zero_counts()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), Counts);

        await Assert.That(comment).Contains("Plan: 3 resource change(s) (2 create, 1 delete).");
    }

    [Test]
    public async Task Still_renders_when_the_counts_are_missing()
    {
        var comment = CommentRenderer.Render(NotReviewedResult(), null);

        await Assert.That(comment).DoesNotContain("Plan:");
        await Assert.That(comment).Contains("**Not reviewed**");
    }

    [Test]
    public async Task Never_presents_no_findings_as_approval()
    {
        var result = new ReviewResult(1, Commit, Verdict.NoFindings, [], [], new Dictionary<string, int>());

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains("**No findings**");
        await Assert.That(comment).Contains("finding nothing doesn't mean a change is approved");
    }

    [Test]
    public async Task Keeps_a_findings_free_text_out_until_it_can_be_escaped()
    {
        var finding = new Finding(
            "azurerm_x.y", "ADR-002", Severity.Medium, Reporter.Conventions, false,
            "![tracker](https://evil.example/x.png)", []);
        var result = new ReviewResult(1, Commit, Verdict.Findings, [finding], [], new Dictionary<string, int>());

        var comment = CommentRenderer.Render(result, Counts);

        await Assert.That(comment).Contains("**1 finding(s)**");
        await Assert.That(comment).DoesNotContain("evil.example");
    }

    private static ReviewResult NotReviewedResult() =>
        ReviewResult.Failed(Commit, ReviewScope.Conventions, NotReviewedReason.Unavailable);
}
