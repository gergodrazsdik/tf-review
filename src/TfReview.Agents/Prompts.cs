using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TfReview.Agents;

/// <summary>A versioned prompt. The version is logged per call and, from M4, is part of the result cache key.</summary>
public sealed record Prompt(string Agent, int Version, string Body)
{
    /// <summary>Hashes the body, which a test compares with prompts/versions.json so a change can't skip a version bump.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body)));
}

/// <summary>The prompts in prompts/, embedded in the assembly because the image's filesystem is read-only.</summary>
public static partial class Prompts
{
    private const string ResourcePrefix = "prompts.";
    private const string ResourceSuffix = ".md";

    public static Prompt Load(string agent)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{ResourcePrefix}{agent}{ResourceSuffix}")
            ?? throw new InvalidOperationException($"There's no prompt for {agent}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The agents that have a prompt.</summary>
    public static IReadOnlyList<string> Agents() =>
    [
        .. Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^ResourceSuffix.Length])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Reads a prompt's header and body. Line endings are normalized, so the hash doesn't depend on them.</summary>
    public static Prompt Parse(string text)
    {
        var match = Header().Match(text.ReplaceLineEndings("\n"));
        if (!match.Success)
        {
            throw new InvalidOperationException("A prompt must start with a header naming its agent and version.");
        }

        return new Prompt(match.Groups["agent"].Value, int.Parse(match.Groups["version"].Value), match.Groups["body"].Value.Trim());
    }

    [GeneratedRegex(@"\A---\nagent: (?<agent>[a-z_]+)\nversion: (?<version>[1-9][0-9]*)\n---\n(?<body>.*)\z", RegexOptions.Singleline)]
    private static partial Regex Header();
}
