using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TfReview.Core;

namespace TfReview.GitHub;

/// <summary>A finding a reviewer ticked as wrong in the comment (D56).</summary>
/// <param name="Key">The finding's key, which stays the same across pushes.</param>
/// <param name="Rule">Its rule, when it's a plain identifier, for the metric.</param>
/// <param name="Recorded">Whether an earlier rewrite already counted it.</param>
public sealed record Dismissal(string Key, string? Rule, bool Recorded);

/// <summary>
/// The comment's dismiss checkboxes (D56). A reviewer with write access ticks one, and the next rewrite of the comment
/// reads it. The hidden marker holds only a hash and a rule ID, never the address: an address comes from the plan, and
/// one containing <c>--&gt;</c> could otherwise end the HTML comment early.
/// </summary>
public static partial class Dismissals
{
    private const string Prefix = "tf-review:dismiss";

    /// <summary>The same problem keeps the same key from push to push: its resource and rule.</summary>
    public static string Key(Finding finding) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{finding.ResourceAddress}\n{finding.Rule}")))[..16];

    /// <summary>The marker for one checkbox line.</summary>
    public static string Marker(Finding finding, bool recorded)
    {
        var rule = RuleId().IsMatch(finding.Rule) ? $" rule={finding.Rule}" : string.Empty;
        return $"<!-- {Prefix} {Key(finding)}{rule}{(recorded ? " recorded" : string.Empty)} -->";
    }

    /// <summary>Every ticked dismiss line in a comment body. Unticked ones and any other text are ignored.</summary>
    public static IReadOnlyList<Dismissal> Ticked(string? body) =>
        body is null
            ? []
            : [
                .. TickedLine().Matches(body).Select(match => new Dismissal(
                    match.Groups["key"].Value,
                    match.Groups["rule"].Success ? match.Groups["rule"].Value : null,
                    match.Groups["recorded"].Success)),
            ];

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex RuleId();

    [GeneratedRegex(@"^- \[[xX]\] .*<!-- tf-review:dismiss (?<key>[0-9a-f]{16})(?: rule=(?<rule>[A-Za-z0-9_.-]{1,64}))?(?<recorded> recorded)? -->\s*$", RegexOptions.Multiline)]
    private static partial Regex TickedLine();
}
