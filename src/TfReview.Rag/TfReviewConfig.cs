using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TfReview.Rag;

public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// The parts of a repo's <c>.tf-review.yml</c> the review uses, as
/// docs/contracts/tf-review-config.schema.json defines them. It's read from the PR's base commit (D27).
/// </summary>
public sealed partial record TfReviewConfig(string Repository, string Commit, IReadOnlyList<string> Agents, bool Blocking)
{
    /// <summary>Whether agents get imported reference material as well as the team's rules (D42). Off by default.</summary>
    public bool References { get; init; }

    private static readonly string[] KnownAgents = ["conventions", "cost", "security"];

    // A repo opts in to the rest: cost needs a secret set up first, and security can add findings a repo's
    // PRs didn't have, so neither should appear because the reviewer's version moved.
    private static readonly string[] DefaultAgents = ["conventions"];

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Reads a config, throwing <see cref="ConfigException"/> on anything the contract doesn't allow.</summary>
    public static TfReviewConfig Parse(string yaml)
    {
        ConfigFile? file;
        try
        {
            file = Yaml.Deserialize<ConfigFile>(yaml);
        }
        catch (YamlException)
        {
            // The message can quote the file, and this text can reach a public comment.
            throw new ConfigException("The config isn't valid YAML, or has an unknown field.");
        }

        if (file is null || file.Version != 1)
        {
            throw new ConfigException("The config must have version: 1.");
        }

        if (file.Standards?.Repository is not { } repository || !RepositoryName().IsMatch(repository))
        {
            throw new ConfigException("standards.repository must be owner/name.");
        }

        if (file.Standards.Commit is not { } commit || !CommitSha().IsMatch(commit))
        {
            throw new ConfigException("standards.commit must be a full commit SHA.");
        }

        var agents = file.Agents ?? [.. DefaultAgents];
        if (agents.Any(agent => !KnownAgents.Contains(agent, StringComparer.Ordinal)))
        {
            throw new ConfigException("agents has an agent that doesn't exist yet.");
        }

        var blocking = file.Gate?.Mode switch
        {
            null or "advisory" => false,
            "blocking" => true,
            _ => throw new ConfigException("gate.mode must be advisory or blocking."),
        };

        return new TfReviewConfig(repository, commit, agents, blocking) { References = file.References ?? false };
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    private sealed class ConfigFile
    {
        public int? Version { get; set; }

        public string? ToolVersion { get; set; }

        public StandardsSection? Standards { get; set; }

        public List<string>? Agents { get; set; }

        public bool? References { get; set; }

        public ThresholdsSection? Thresholds { get; set; }

        public GateSection? Gate { get; set; }
    }

    private sealed class StandardsSection
    {
        public string? Repository { get; set; }

        public string? Commit { get; set; }
    }

    private sealed class ThresholdsSection
    {
        public string? CommentMinSeverity { get; set; }
    }

    private sealed class GateSection
    {
        public string? Mode { get; set; }
    }
}
