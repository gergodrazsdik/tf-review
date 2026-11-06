using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents;

/// <summary>Findings with any sentence added, the references each was offered, and the calls that took.</summary>
public sealed record SuggestionPass(
    IReadOnlyList<Finding> Findings, IReadOnlyList<ReferenceRetrieval> Retrievals, IReadOnlyList<AgentCall> Calls, int? PromptVersion);

/// <summary>
/// Adds to a finding's suggestion from references searched for that finding alone (D50). Chosen per agent, references
/// were about the resources rather than the problem, and a prompt asking agents to use them added sentences beside the
/// point. Here each finding gets its own search, by its standard, type and attribute, and the model may add one sentence.
/// Code keeps it only if it names a term the cited excerpt contains, isn't the finding's own attribute, doesn't recommend
/// Azure Policy, and the model says it addresses the same problem. The finding itself never changes, so references still can't create one (D42).
/// </summary>
public sealed partial class ReferenceSuggester(IReferenceRetriever retriever, IChatClient chat, TimeSpan? attemptTimeout = null)
{
    public const string Name = "suggester";

    /// <summary>Per finding, about two excerpts' worth.</summary>
    public const int TokenBudget = 1_500;

    private const int Candidates = 4;

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "addition": { "type": ["string", "null"] },
            "reference_id": { "type": ["string", "null"] },
            "term": { "type": ["string", "null"] },
            "same_problem": { "type": "boolean" }
          },
          "required": ["addition", "reference_id", "term", "same_problem"],
          "additionalProperties": false
        }
        """;

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromSeconds(45);

    /// <param name="findings">The review's findings. Only those citing a team standard are considered.</param>
    /// <param name="plan">The plan, for each resource's type and actions.</param>
    /// <param name="docs">The team's standards, for each one's title.</param>
    /// <param name="providerVersion">The lock file's azurerm version, which provider docs are filtered on.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public async Task<SuggestionPass> SuggestAsync(
        IReadOnlyList<Finding> findings,
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        string? providerVersion,
        CancellationToken cancellationToken = default)
    {
        var prompt = Prompts.Load(Name);
        var results = await Task.WhenAll(findings.Select(finding => this.OneAsync(finding, plan, docs, providerVersion, prompt, cancellationToken)));
        return new SuggestionPass(
            [.. results.Select(result => result.Finding)],
            [.. results.Select(result => result.Retrieval).OfType<ReferenceRetrieval>()],
            [.. results.SelectMany(result => result.Calls)],
            prompt.Version);
    }

    /// <summary>The top-level attribute a finding's plan pointer names, or null.</summary>
    public static string? AttributeOf(Finding finding)
    {
        foreach (var item in finding.Evidence)
        {
            if (item.Value is PlanEvidence plan && plan.PlanPointer.Split('/') is [.., "after" or "before", var attribute])
            {
                return attribute.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an addition stands: from an offered excerpt, naming a term that excerpt contains, not the finding's own
    /// attribute or a term the suggestion already has, and about the same problem by the model's own reading.
    /// </summary>
    public static string? Accept(WireOutput? answer, IReadOnlyList<ReferenceDoc> offered, string? attribute, string? suggestion)
    {
        if (answer is not { SameProblem: true, Addition: { } addition, ReferenceId: { } id, Term: { } term }
            || addition.Trim().Length == 0
            || offered.FirstOrDefault(doc => doc.Id == id) is not { } doc)
        {
            return null;
        }

        // Measured after normalising, so a term of punctuation alone, which matches anything, can't pass (BUG-4). The
        // sentence itself has to name it, or the check would be of a term the model never used.
        var key = Normalize(term);
        if (key.Length < 3
            || !Normalize(addition).Contains(key, StringComparison.Ordinal)
            || !Normalize(doc.Text).Contains(key, StringComparison.Ordinal)
            || (attribute is not null && Normalize(attribute) == key)
            || (suggestion is not null && Normalize(suggestion).Contains(key, StringComparison.Ordinal))
            || Governance().IsMatch(addition))
        {
            return null;
        }

        return addition.Trim();
    }

    // Case, spaces, dashes and underscores differ between an ARM name, an azurerm name and prose.
    private static string Normalize(string text) => NotLetterOrDigit().Replace(text.ToLowerInvariant(), string.Empty);

    private async Task<(Finding Finding, ReferenceRetrieval? Retrieval, IReadOnlyList<AgentCall> Calls)> OneAsync(
        Finding finding,
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        string? providerVersion,
        Prompt prompt,
        CancellationToken cancellationToken)
    {
        // Past the review's time a suggestion isn't worth a search: the finding stands without one.
        if (finding.Agent == Reporter.Scanner
            || chat.GetService<ReviewClock>()?.Expired == true
            || docs.FirstOrDefault(doc => doc.Id == finding.Rule) is not { } standard
            || plan.TypeOf(finding.ResourceAddress) is not { } type)
        {
            return (finding, null, []);
        }

        var attribute = AttributeOf(finding);
        var title = Title(standard);

        // Built by code from the standard, the type and the attribute's name, never values, like the per-agent query.
        var query = new ReferenceQuery(
            $"{title}. Resource type: {type}.{(attribute is null ? string.Empty : $" Attribute: {attribute}.")}",
            [type],
            providerVersion,
            TokenBudget,
            ReferenceRanking.Hybrid,
            Candidates);

        ReferenceRetrieval retrieval;
        try
        {
            retrieval = await retriever.RetrieveAsync(query, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (finding, null, []);
        }

        if (retrieval.Docs.Count == 0)
        {
            return (finding, retrieval, []);
        }

        var change = plan.Changes().FirstOrDefault(item => item.Address == finding.ResourceAddress);
        var data = new JsonObject
        {
            ["standard"] = title,
            ["resource_type"] = type,
            ["actions"] = new JsonArray([.. (change?.Actions ?? []).Select(action => (JsonNode)JsonValue.Create(action)!)]),
            ["attribute"] = attribute,
            ["title"] = finding.Title,
            ["suggestion"] = finding.Suggestion,
        };
        var references = new JsonArray(
        [
            .. retrieval.Docs.OrderBy(doc => doc.Id, StringComparer.Ordinal).Select(doc => (JsonNode)new JsonObject
            {
                ["id"] = doc.Id,
                ["title"] = doc.Title,
                ["heading"] = doc.Heading,
                ["text"] = doc.Text,
            }),
        ]);
        var user = $"""
            <finding>
            {StructuredCall.Escape(CanonicalJson.Write(data))}
            </finding>

            <references>
            {StructuredCall.Escape(CanonicalJson.Write(references))}
            </references>
            """;

        var result = await StructuredCall.RunAsync(
            chat, Name, prompt.Body, user, Schema, StructuredCall.ParseWire<WireOutput>, timeout, cancellationToken);
        var addition = Accept(result.Value, retrieval.Docs, attribute, finding.Suggestion);
        var suggested = addition is null
            ? finding
            : finding with { Suggestion = finding.Suggestion is null ? addition : $"{finding.Suggestion} {addition}" };
        return (suggested, retrieval, result.Calls);
    }

    // The heading the doc starts with, such as "POL-001: Storage account hardening", or else its ID.
    private static string Title(StandardsDoc doc) =>
        doc.Body.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal)) is { } heading
            ? heading[2..].Trim()
            : doc.Id;

    // The team's standards are its policy layer. Asked not to, the model still advised Azure Policy to allow only some VM
    // sizes on every resize, from guidance and policy excerpts alike, so code drops any sentence recommending it.
    [GeneratedRegex(@"\bAzure\s+polic(y|ies)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Governance();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotLetterOrDigit();

    public sealed record WireOutput(
        [property: JsonPropertyName("addition")] string? Addition,
        [property: JsonPropertyName("reference_id")] string? ReferenceId,
        [property: JsonPropertyName("term")] string? Term,
        [property: JsonPropertyName("same_problem")] bool SameProblem);
}
