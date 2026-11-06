using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TfReview.Core;

namespace TfReview.Eval;

/// <summary>
/// Decides whether a review of a seeded case passed, and scores its suggestions. Kept apart from the runner so
/// TfReview.Eval.Tests can test it: every baseline number rests on it.
/// </summary>
internal static class Judge
{
    /// <summary>
    /// What's wrong with a result. The whole set has to match: an extra finding is a false positive, and it fails the
    /// case like a missed one, and so does the same finding twice.
    /// </summary>
    public static List<string> Problems(ExpectedCase expected, ReviewResult result)
    {
        var problems = new List<string>();

        foreach (var missing in result.NotReviewed)
        {
            problems.Add($"not reviewed: {missing.Scope} ({missing.Reason})");
        }

        var actual = result.Findings
            .Select(finding => new ExpectedFinding(finding.ResourceAddress, finding.Rule, Name(finding.Agent), finding.Deterministic))
            .ToList();

        // An arbiter can merge rows about one problem, so a rule it folded into another row still counts as reported.
        var folded = result.Findings
            .SelectMany(finding => (finding.RelatedRules ?? []).Select(rule => (finding.ResourceAddress, Rule: rule)))
            .ToHashSet();

        problems.AddRange(expected.Findings.Except(actual)
            .Where(finding => !folded.Contains((finding.ResourceAddress, finding.Rule)))
            .Select(finding => $"missing: {finding}"));

        // With the wording, because a false positive's reasoning is what a prompt fix needs. Evals only see fixtures,
        // so nothing private reaches these logs.
        problems.AddRange(result.Findings
            .Where(finding => !expected.Findings.Contains(new ExpectedFinding(finding.ResourceAddress, finding.Rule, Name(finding.Agent), finding.Deterministic)))
            .Select(finding => $"unexpected: {finding.ResourceAddress} {finding.Rule} from {Name(finding.Agent)}, deterministic {finding.Deterministic}: {finding.Title}"));

        problems.AddRange(actual
            .GroupBy(finding => finding)
            .Where(group => group.Count() > 1)
            .Select(group => $"reported {group.Count()} times: {group.Key}"));

        if (expected.RiskFirst is { } riskFirst && result.RiskOrder?.FirstOrDefault()?.ResourceAddress != riskFirst)
        {
            problems.Add($"risk order: expected {riskFirst} first, and got {result.RiskOrder?.FirstOrDefault()?.ResourceAddress ?? "nothing"}");
        }

        if (expected.FirstFinding is { } firstFinding && result.Findings.FirstOrDefault()?.ResourceAddress != firstFinding)
        {
            problems.Add($"expected a finding on {firstFinding} first, and got {result.Findings.FirstOrDefault()?.ResourceAddress ?? "nothing"}");
        }

        foreach (var finding in result.Findings)
        {
            if (!finding.Evidence.Any(item => item.Value is PlanEvidence))
            {
                problems.Add($"{finding.ResourceAddress} {finding.Rule}: no plan pointer");
            }

            // A scanner's own rule ID has no document behind it. Anything citing a standard has to name it.
            var citesStandard = Regex.IsMatch(finding.Rule, "^(ADR|POL|DES)-[0-9]{3}$");
            if (citesStandard && !finding.Evidence.Any(item => item.Value is DocEvidence doc && doc.DocId == finding.Rule))
            {
                problems.Add($"{finding.ResourceAddress} {finding.Rule}: no citation");
            }

            if (finding.Agent == Reporter.Cost && finding.MonthlyCostDeltaUsd is not > 0)
            {
                problems.Add($"{finding.ResourceAddress} {finding.Rule}: no cost figure");
            }
        }

        return problems;
    }

    public static string Name(Reporter reporter) => JsonSerializer.Serialize(reporter).Trim('"');
}

/// <summary>A case's expected result.</summary>
/// <param name="Findings">Every finding the review should report, and nothing else.</param>
internal sealed record ExpectedCase([property: JsonPropertyName("findings")] IReadOnlyList<ExpectedFinding> Findings)
{
    [JsonPropertyName("risk_first")]
    public string? RiskFirst { get; init; }

    [JsonPropertyName("first_finding")]
    public string? FirstFinding { get; init; }

    /// <summary>
    /// True only while nothing has been changed after reading this case's answers or failures: not a prompt, code or
    /// its suggestion patterns. Once something has, the flag goes, as for C13, and the case is a regression case.
    /// Reports count held-out and tuned cases apart, because only the first say how the reviewer does on new plans.
    /// </summary>
    [JsonPropertyName("held_out")]
    public bool HeldOut { get; init; }

    public static ExpectedCase Load(string directory) =>
        JsonSerializer.Deserialize<ExpectedCase>(File.ReadAllText(Path.Combine(directory, "expected.json")))!;
}

internal sealed record ExpectedFinding(
    [property: JsonPropertyName("resource_address")] string ResourceAddress,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("deterministic")] bool Deterministic);

/// <summary>A finding as the scorer sees it, from a live result or a saved run.</summary>
internal sealed record ScoredFinding(
    [property: JsonPropertyName("ResourceAddress")] string ResourceAddress,
    [property: JsonPropertyName("Rule")] string Rule,
    [property: JsonPropertyName("Suggestion")] string? Suggestion);

/// <summary>
/// What a good suggestion for each expected finding should mention, as case-insensitive patterns. Patterns were fixed
/// before any model saw a case, and changed since only to accept other wording of the same concept or to stop
/// accepting wording that isn't it, with every saved run rescored each time (docs/evals/baseline.md). A finding that's
/// missing scores nothing for its concepts.
/// </summary>
internal sealed record SuggestionScore([property: JsonPropertyName("findings")] IReadOnlyList<SuggestionExpectation> Findings)
{
    public static SuggestionScore? Load(string directory)
    {
        var path = Path.Combine(directory, "suggestions.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SuggestionScore>(File.ReadAllText(path)) : null;
    }

    /// <summary>Whether a suggestion expresses a concept: the pattern matches anywhere, ignoring case.</summary>
    public static bool Mentions(string? suggestion, string pattern) =>
        suggestion is not null && Regex.IsMatch(suggestion, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public (int Found, int Asked, IReadOnlyList<string> Missed) Score(IEnumerable<Finding> findings) =>
        this.Score(findings.Select(finding => new ScoredFinding(finding.ResourceAddress, finding.Rule, finding.Suggestion)));

    public (int Found, int Asked, IReadOnlyList<string> Missed) Score(IEnumerable<ScoredFinding> findings)
    {
        var found = 0;
        var asked = 0;
        var missed = new List<string>();
        var list = findings.ToList();
        foreach (var expectation in this.Findings)
        {
            var finding = list.FirstOrDefault(item => item.ResourceAddress == expectation.ResourceAddress && item.Rule == expectation.Rule);
            foreach (var (name, pattern) in expectation.Concepts)
            {
                asked++;
                if (Mentions(finding?.Suggestion, pattern))
                {
                    found++;
                }
                else
                {
                    missed.Add($"{expectation.Rule} {name}");
                }
            }
        }

        return (found, asked, missed);
    }
}

internal sealed record SuggestionExpectation(
    [property: JsonPropertyName("resource_address")] string ResourceAddress,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("concepts")] IReadOnlyDictionary<string, string> Concepts);

/// <summary>Running totals, kept apart for held-out and tuned cases.</summary>
internal sealed class Tally
{
    private readonly Dictionary<bool, (int Cases, int Passed, int Runs, int Found, int Asked)> totals = [];

    public void Add(bool heldOut, int passed, int runs, int found, int asked)
    {
        var (cases, p, r, f, a) = this.totals.GetValueOrDefault(heldOut);
        this.totals[heldOut] = (cases + 1, p + passed, r + runs, f + found, a + asked);
    }

    /// <summary>One line each for held-out and tuned cases, or nothing for a kind with no cases.</summary>
    public IEnumerable<string> Lines(bool withRuns = true)
    {
        foreach (var heldOut in new[] { true, false })
        {
            if (this.totals.GetValueOrDefault(heldOut) is not { Cases: > 0 } total)
            {
                continue;
            }

            var label = heldOut ? "Held out" : "Tuned on or seen";
            var runs = withRuns ? $", runs passing {total.Passed} of {total.Runs}" : string.Empty;
            var concepts = total.Asked > 0 ? $", suggestion concepts {total.Found} of {total.Asked}" : string.Empty;
            yield return $"{label}: {total.Cases} case(s){runs}{concepts}.";
        }
    }
}
