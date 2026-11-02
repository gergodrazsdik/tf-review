namespace TfReview.Scanners.Tests;

public class ProcessRunnerTests
{
    private const string Secret = "TF_REVIEW_TEST_SECRET";
    private const string Needed = "TF_REVIEW_TEST_NEEDED";

    [Test]
    public async Task A_scanner_gets_only_the_environment_it_needs()
    {
        Environment.SetEnvironmentVariable(Secret, "secret");
        Environment.SetEnvironmentVariable(Needed, "needed");

        var result = await new ProcessRunner(Needed).RunAsync("env", [], TimeSpan.FromSeconds(10));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Output).DoesNotContain(Secret);
        await Assert.That(result.Output).Contains($"{Needed}=needed");
        await Assert.That(result.Output).Contains("PATH=");
    }

    [Test]
    public async Task The_identity_endpoint_doesnt_reach_a_scanner()
    {
        Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", "http://localhost:42356/msi/token");
        Environment.SetEnvironmentVariable("IDENTITY_HEADER", "header");

        var result = await new ProcessRunner().RunAsync("env", [], TimeSpan.FromSeconds(10));

        await Assert.That(result!.Output).DoesNotContain("IDENTITY_");
    }
}
