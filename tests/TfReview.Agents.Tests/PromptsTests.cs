using System.Text.Json;

namespace TfReview.Agents.Tests;

public class PromptsTests
{
    private const string Versions = "prompts/versions.json";

    /// <summary>
    /// Fails when a prompt changes without a version bump. To change a prompt: raise <c>version</c> in its
    /// header and add the new hash to prompts/versions.json, leaving the older entries as they are.
    /// </summary>
    [Test]
    public async Task Every_prompt_matches_the_hash_recorded_for_its_version()
    {
        var recorded = await ReadRecorded();

        foreach (var agent in Prompts.Agents())
        {
            var problem = VersionProblem(Prompts.Load(agent), recorded.GetValueOrDefault(agent) ?? []);
            await Assert.That(problem).IsNull();
        }
    }

    [Test]
    public async Task The_check_catches_a_prompt_changed_without_a_bump()
    {
        var prompt = new Prompt("a", 1, "changed");
        var recorded = new Dictionary<string, string> { ["1"] = new Prompt("a", 1, "original").Hash() };

        await Assert.That(VersionProblem(prompt, recorded)).Contains("without a version bump");
    }

    [Test]
    public async Task The_check_catches_a_bump_with_no_recorded_hash()
    {
        var recorded = new Dictionary<string, string> { ["1"] = new Prompt("a", 1, "original").Hash() };

        await Assert.That(VersionProblem(new Prompt("a", 2, "changed"), recorded)).Contains("no recorded hash");
    }

    [Test]
    public async Task The_check_catches_a_version_moved_backwards()
    {
        var old = new Prompt("a", 1, "original");
        var recorded = new Dictionary<string, string> { ["1"] = old.Hash(), ["2"] = new Prompt("a", 2, "newer").Hash() };

        await Assert.That(VersionProblem(old, recorded)).Contains("isn't the newest");
    }

    [Test]
    public async Task The_conventions_prompt_is_embedded_and_names_its_agent()
    {
        var prompt = Prompts.Load(AgentProfile.Conventions.Name);

        await Assert.That(prompt.Agent).IsEqualTo(AgentProfile.Conventions.Name);
        await Assert.That(prompt.Body).DoesNotContain("version:");
    }

    [Test]
    public async Task Line_endings_dont_change_the_hash()
    {
        const string Text = "---\nagent: a\nversion: 1\n---\nLine one.\nLine two.\n";

        await Assert.That(Prompts.Parse(Text.ReplaceLineEndings("\r\n")).Hash()).IsEqualTo(Prompts.Parse(Text).Hash());
    }

    [Test]
    [Arguments("No header.")]
    [Arguments("---\nagent: a\n---\nBody")]
    [Arguments("---\nagent: a\nversion: 0\n---\nBody")]
    public async Task Refuses_a_prompt_without_a_valid_header(string text)
    {
        await Assert.That(() => Prompts.Parse(text)).Throws<InvalidOperationException>();
    }

    private static string? VersionProblem(Prompt prompt, IReadOnlyDictionary<string, string> recorded)
    {
        var version = prompt.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!recorded.TryGetValue(version, out var hash))
        {
            return $"{prompt.Agent} v{version} has no recorded hash. Add {prompt.Hash()} to {Versions}.";
        }

        if (hash != prompt.Hash())
        {
            return $"{prompt.Agent} changed without a version bump. Raise its version and add {prompt.Hash()} to {Versions}.";
        }

        return prompt.Version == recorded.Keys.Max(int.Parse) ? null : $"{prompt.Agent} v{version} isn't the newest recorded version.";
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> ReadRecorded() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(Versions))
        ?? throw new JsonException($"{Versions} is empty.");
}
