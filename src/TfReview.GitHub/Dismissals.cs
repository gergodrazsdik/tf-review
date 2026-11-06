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
/// One dismiss line in a comment: a shown checkbox, or a hidden line carrying a tick or a count for a finding the comment
/// doesn't show at the moment (D56).
/// </summary>
/// <param name="Keys">The finding's keys: its own first, then those of rules merged into its row.</param>
/// <param name="Rule">Its rule, when it's a plain identifier, for the metric.</param>
/// <param name="Ticked">Whether it's ticked.</param>
/// <param name="Recorded">Whether a rewrite already counted it, which stays when it's unticked, so a tick again isn't
/// counted twice.</param>
public sealed record DismissLine(IReadOnlyList<string> Keys, string? Rule, bool Ticked, bool Recorded);

/// <summary>
/// The comment's dismiss checkboxes (D56). A reviewer with write access ticks one, and the next rewrite of the comment
/// reads it. The hidden marker holds only hashes and a rule ID, never the address: an address comes from the plan, and
/// one containing <c>--&gt;</c> could otherwise end the HTML comment early.
/// </summary>
public static partial class Dismissals
{
    private const string Prefix = "tf-review:dismiss";

    /// <summary>Hidden lines kept for findings that aren't shown, beyond which the oldest are dropped.</summary>
    private const int MaxHidden = 100;

    /// <summary>Keys on one line: a row merges few rules.</summary>
    private const int MaxKeys = 8;

    /// <summary>The same problem keeps the same key from push to push: its resource and rule.</summary>
    public static string Key(Finding finding) => Key(finding.ResourceAddress, finding.Rule);

    /// <summary>
    /// A finding's own key, then one per rule merged into its row, so a tick survives the arbiter merging rows or not
    /// merging them on another push (BUG-23).
    /// </summary>
    public static IReadOnlyList<string> Keys(Finding finding) =>
        [Key(finding), .. (finding.RelatedRules ?? []).Select(rule => Key(finding.ResourceAddress, rule))];

    /// <summary>The marker for one checkbox line.</summary>
    public static string Marker(Finding finding, bool recorded) => Marker(Keys(finding), finding.Rule, recorded);

    /// <summary>Every ticked dismiss line in a comment body, shown or hidden. Unticked ones and any other text are ignored.</summary>
    public static IReadOnlyList<Dismissal> Ticked(string? body) =>
        [.. Read(body).Where(line => line.Ticked).Select(line => new Dismissal(line.Keys[0], line.Rule, line.Recorded))];

    /// <summary>Every dismiss line in a comment body, shown or hidden, ticked or not.</summary>
    public static IReadOnlyList<DismissLine> Read(string? body)
    {
        if (body is null)
        {
            return [];
        }

        return
        [
            .. ShownLine().Matches(body).Select(match => Line(match, match.Groups["tick"].Value is "x" or "X")),
            .. HiddenLine().Matches(body).Select(match => Line(match, match.Groups["ticked"].Success)),
        ];
    }

    /// <summary>
    /// What a rewrite does with the previous comment's lines, given the findings it shows: each shown one is ticked if
    /// any of its keys was, and the rest that were ticked or counted stay as hidden lines, so a "not reviewed", a "no
    /// findings" or a finding missing for a push doesn't lose them. With <paramref name="record"/>, each tick not yet
    /// counted is marked as counted and returned, for the caller to count once the comment is written.
    /// </summary>
    public static DismissPlan Plan(IEnumerable<Finding> shown, string? previous, bool record, int maxCounted)
    {
        var lines = Read(previous);
        var counted = new List<string?>();
        bool Count(string? rule)
        {
            if (!record || counted.Count >= maxCounted)
            {
                return false;
            }

            counted.Add(rule);
            return true;
        }

        var used = new HashSet<DismissLine>();
        var markers = new Dictionary<Finding, (bool Ticked, string Marker)>(ReferenceEqualityComparer.Instance);
        foreach (var finding in shown)
        {
            var keys = Keys(finding);
            var matching = lines.Where(line => line.Keys.Any(key => keys.Contains(key, StringComparer.Ordinal))).ToList();
            used.UnionWith(matching);

            var ticked = matching.Any(line => line.Ticked);
            var recorded = matching.Any(line => line.Recorded) || (ticked && Count(finding.Rule));
            var allKeys = keys.Concat(matching.SelectMany(line => line.Keys)).Distinct(StringComparer.Ordinal).Take(MaxKeys).ToList();
            markers[finding] = (ticked, Marker(allKeys, finding.Rule, recorded));
        }

        var hidden = new List<string>();
        foreach (var line in lines.Where(line => !used.Contains(line) && (line.Ticked || line.Recorded)).TakeLast(MaxHidden))
        {
            var recorded = line.Recorded || (line.Ticked && Count(line.Rule));
            hidden.Add(Hidden(line with { Recorded = recorded }));
        }

        return new DismissPlan(markers, hidden, counted);
    }

    private static string Key(string address, string rule) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{address}\n{rule}")))[..16];

    private static string Marker(IReadOnlyList<string> keys, string rule, bool recorded) =>
        $"<!-- {Prefix} {keys[0]}{Also(keys)}{Rule(rule)}{(recorded ? " recorded" : string.Empty)} -->";

    // Its own prefix, so the feedback script, which reads shown lines, never takes it for one.
    private static string Hidden(DismissLine line) =>
        $"<!-- {Prefix}ed {line.Keys[0]}{Also(line.Keys)}{Rule(line.Rule)}{(line.Ticked ? " ticked" : string.Empty)}{(line.Recorded ? " recorded" : string.Empty)} -->";

    private static string Also(IReadOnlyList<string> keys) => keys.Count > 1 ? $" also={string.Join(',', keys.Skip(1))}" : string.Empty;

    private static string Rule(string? rule) => rule is not null && RuleId().IsMatch(rule) ? $" rule={rule}" : string.Empty;

    private static DismissLine Line(Match match, bool ticked) => new(
        [match.Groups["key"].Value, .. match.Groups["also"].Success ? match.Groups["also"].Value.Split(',') : []],
        match.Groups["rule"].Success ? match.Groups["rule"].Value : null,
        ticked,
        match.Groups["recorded"].Success);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex RuleId();

    [GeneratedRegex(@"^- \[(?<tick>[ xX])\] .*<!-- tf-review:dismiss (?<key>[0-9a-f]{16})(?: also=(?<also>[0-9a-f]{16}(?:,[0-9a-f]{16}){0,7}))?(?: rule=(?<rule>[A-Za-z0-9_.-]{1,64}))?(?<recorded> recorded)? -->\s*$", RegexOptions.Multiline)]
    private static partial Regex ShownLine();

    [GeneratedRegex(@"^<!-- tf-review:dismissed (?<key>[0-9a-f]{16})(?: also=(?<also>[0-9a-f]{16}(?:,[0-9a-f]{16}){0,7}))?(?: rule=(?<rule>[A-Za-z0-9_.-]{1,64}))?(?<ticked> ticked)?(?<recorded> recorded)? -->\s*$", RegexOptions.Multiline)]
    private static partial Regex HiddenLine();
}

/// <summary>A rewrite's dismiss lines: each shown finding's tick and marker, the hidden lines, and the rules newly counted.</summary>
public sealed record DismissPlan(
    IReadOnlyDictionary<Finding, (bool Ticked, string Marker)> Shown, IReadOnlyList<string> Hidden, IReadOnlyList<string?> Counted);
