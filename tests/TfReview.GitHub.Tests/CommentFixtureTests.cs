using TfReview.Core;

namespace TfReview.GitHub.Tests;

/// <summary>
/// Keeps tests/fixtures/comments/review.md equal to what the renderer writes, because scripts/service_eval.py and
/// scripts/feedback_cases.py parse that format and their tests read this file. A renderer change fails here first.
/// </summary>
public class CommentFixtureTests
{
    [Test]
    public async Task The_parsers_fixture_is_what_the_renderer_writes()
    {
        var blocking = new Finding(
            "azurerm_network_security_rule.ssh", "POL-002", Severity.High, Reporter.Security, true, "POL-002: SSH is open to the internet",
            [new DocEvidence("POL-002"), new PlanEvidence("/resource_changes/0/change/after/source_address_prefix")])
        {
            Blocking = true,
            Suggestion = "Allow SSH only from the bastion subnet | or a VPN range.",
            RelatedRules = ["CKV_AZURE_10"],
        };
        var model = new Finding(
            "azurerm_storage_account.logs", "ADR-001", Severity.Low, Reporter.Conventions, false, "Name `stlogs` has no environment",
            [new DocEvidence("ADR-001"), new PlanEvidence("/resource_changes/1/change/after/name")])
        {
            Suggestion = "Rename it to stlogsprod.",
        };
        var scanner = new Finding(
            "azurerm_storage_account.logs", "CKV_AZURE_59", Severity.Medium, Reporter.Scanner, true, "Public network access is enabled",
            [new PlanEvidence("/resource_changes/1/change/after/public_network_access_enabled")]);
        var result = new ReviewResult(
            1,
            "0123456789abcdef0123456789abcdef01234567",
            Verdict.Findings,
            [blocking, scanner, model],
            [new NotReviewed(ReviewScope.Cost, NotReviewedReason.PlanTooLarge) { Resources = ["azurerm_linux_virtual_machine.big"] }],
            new Dictionary<string, int>())
        {
            Gate = GateMode.Blocking,
            StandardsRepository = "gergodrazsdik/tf-standards",
            StandardsCommit = "adf50af6607b4e2b28a7e3566db8adfdecabe683",
            DocPaths = new Dictionary<string, string> { ["POL-002"] = "policies/POL-002-network.md" },
        };

        var comment = CommentRenderer.Render(result, null, new HashSet<string> { Dismissals.Key(model) }).ReplaceLineEndings("\n");
        var fixture = (await File.ReadAllTextAsync("comments/review.md")).ReplaceLineEndings("\n");

        if (comment != fixture)
        {
            await File.WriteAllTextAsync("comments/review.actual.md", comment);
        }

        await Assert.That(comment).IsEqualTo(fixture)
            .Because("the renderer changed: check the Python parsers, then copy comments/review.actual.md from the test's output over tests/fixtures/comments/review.md");
    }
}
