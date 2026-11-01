namespace TfReview.Rag.Tests;

public class TfReviewConfigTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Test]
    public async Task Reads_the_contract_example()
    {
        var config = TfReviewConfig.Parse(await File.ReadAllTextAsync("contracts/tf-review.yml"));

        await Assert.That(config.Repository).IsEqualTo("gergodrazsdik/tf-standards");
        await Assert.That(config.Commit).IsEqualTo(Sha);
        await Assert.That(config.Blocking).IsFalse();
    }

    [Test]
    public async Task Defaults_to_the_agents_that_exist_and_advisory()
    {
        var config = TfReviewConfig.Parse($"version: 1\nstandards:\n  repository: a/b\n  commit: {Sha}\n");

        await Assert.That(config.Agents).IsEquivalentTo(["conventions"]);
        await Assert.That(config.Blocking).IsFalse();
    }

    [Test]
    public async Task Cost_is_opt_in_because_it_needs_a_secret()
    {
        var config = TfReviewConfig.Parse($"version: 1\nstandards:\n  repository: a/b\n  commit: {Sha}\nagents: [conventions, cost]\n");

        await Assert.That(config.Agents).Contains("cost");
    }

    [Test]
    public async Task Reads_blocking_mode()
    {
        var config = TfReviewConfig.Parse($"version: 1\nstandards:\n  repository: a/b\n  commit: {Sha}\ngate:\n  mode: blocking\n");

        await Assert.That(config.Blocking).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("version: 2\nstandards:\n  repository: a/b\n  commit: 0123456789abcdef0123456789abcdef01234567\n")]
    [Arguments("version: 1\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b\n  commit: main\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b/../../c\n  commit: 0123456789abcdef0123456789abcdef01234567\n")]
    [Arguments("version: 1\nstandards:\n  repository: https://evil.example/x\n  commit: 0123456789abcdef0123456789abcdef01234567\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b\n  commit: 0123456789abcdef0123456789abcdef01234567\nagents: [security]\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b\n  commit: 0123456789abcdef0123456789abcdef01234567\ngate:\n  mode: strict\n")]
    [Arguments("version: 1\nstandards:\n  repository: a/b\n  commit: 0123456789abcdef0123456789abcdef01234567\nunknown: true\n")]
    [Arguments("{not yaml: [")]
    public async Task Rejects_what_the_contract_doesnt_allow(string yaml)
    {
        await Assert.That(() => TfReviewConfig.Parse(yaml)).Throws<ConfigException>();
    }

    [Test]
    public async Task A_bad_config_doesnt_quote_itself_in_the_error()
    {
        var error = await Assert.That(() => TfReviewConfig.Parse("my-secret-looking-value: [")).Throws<ConfigException>();

        await Assert.That(error!.Message).DoesNotContain("my-secret-looking-value");
    }
}
