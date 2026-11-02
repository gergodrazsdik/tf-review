using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents;

/// <summary>One model call, for the logs and the cost metrics. It holds no prompt or answer text.</summary>
public sealed record AgentCall(int Attempt, string Finish, long InputTokens, long OutputTokens, long CachedInputTokens, TimeSpan Latency);

public closed record AgentOutcome(int PromptVersion, IReadOnlyList<AgentCall> Calls);

/// <summary>The model answered. The findings still have to pass the evidence check.</summary>
public sealed record AgentReviewed(int PromptVersion, IReadOnlyList<AgentCall> Calls, IReadOnlyList<Finding> Findings)
    : AgentOutcome(PromptVersion, Calls);

/// <summary>The slice wasn't reviewed, and the comment says so instead of guessing.</summary>
public sealed record AgentFailed(int PromptVersion, IReadOnlyList<AgentCall> Calls, NotReviewedReason Reason)
    : AgentOutcome(PromptVersion, Calls);

/// <summary>
/// Reviews a plan against the standards a profile retrieves. Plan text only ever reaches the user message, and
/// the agent has no tools. The model chooses the resource, document, attribute and wording; code sets the
/// rule, severity, reporter, evidence and any cost figure, so a model can't claim to be deterministic, raise
/// a severity or invent a price.
/// </summary>
public sealed class ReviewAgent(IChatClient chat, AgentProfile profile, TimeSpan? attemptTimeout = null)
{
    private const string SchemaName = "review_output";
    private const int MaxOutputTokens = 4_000;
    private const int MaxAttempts = 2;

    // Above this the 30K TPM limit would reject the call, whatever the retries. M4 splits large plans.
    private const int MaxUserMessageTokens = 20_000;

    // Azure's strict mode needs every field required and rejects pattern and minItems, so this can't be
    // finding.schema.json. scripts/llm-smoke.cs proves this shape against the real model.
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "resource_address": { "type": "string" },
                  "doc_id": { "type": "string" },
                  "attribute": { "type": "string" },
                  "title": { "type": "string" },
                  "suggestion": { "type": ["string", "null"] }
                },
                "required": ["resource_address", "doc_id", "attribute", "title", "suggestion"],
                "additionalProperties": false
              }
            }
          },
          "required": ["findings"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly TimeSpan timeout = attemptTimeout ?? TimeSpan.FromMinutes(3);

    public async Task<AgentOutcome> ReviewAsync(
        CondensedPlan plan,
        IReadOnlyList<StandardsDoc> docs,
        IReadOnlyList<Finding> policyFindings,
        IReadOnlyList<ResourceCostChange>? costs = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = Prompts.Load(profile.Name);
        var calls = new List<AgentCall>();

        // No standard applies, so there's nothing to ask, and no reason to pay for it.
        if (docs.Count == 0)
        {
            return new AgentReviewed(prompt.Version, calls, []);
        }

        var user = UserMessage(plan, docs, policyFindings, costs);
        if (TokenEstimate.Of(user) > MaxUserMessageTokens)
        {
            return new AgentFailed(prompt.Version, calls, NotReviewedReason.PlanTooLarge);
        }

        var system = SystemMessage(prompt, docs);
        var maxOutputTokens = MaxOutputTokens;
        var last = NotReviewedReason.InvalidOutput;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var capture = new ResponseCapture(chat);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                await NewAgent(capture, profile.Name, system, maxOutputTokens).RunAsync(user, cancellationToken: deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                calls.Add(Call(attempt, "timeout", null, stopwatch.Elapsed));
                return new AgentFailed(prompt.Version, calls, NotReviewedReason.Timeout);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The SDK has already retried what's transient. Any failure here has to become a "not
                // reviewed" for this commit, and only the type is logged, as messages can quote the request.
                calls.Add(Call(attempt, e.GetType().Name, null, stopwatch.Elapsed));
                return new AgentFailed(prompt.Version, calls, Classify(e));
            }

            var response = capture.Response;
            calls.Add(Call(attempt, response?.FinishReason?.ToString() ?? "unknown", response, stopwatch.Elapsed));

            if (response?.FinishReason == ChatFinishReason.ContentFilter)
            {
                return new AgentFailed(prompt.Version, calls, NotReviewedReason.ContentFilter);
            }

            if (response is not null && IsRefusal(response))
            {
                return new AgentFailed(prompt.Version, calls, NotReviewedReason.Refused);
            }

            // The same limit would cut it off again, so the one retry gets more room.
            if (response?.FinishReason == ChatFinishReason.Length)
            {
                last = NotReviewedReason.Truncated;
                maxOutputTokens *= 2;
                continue;
            }

            if (response is not null && ParseFindings(response.Text, docs, costs) is { } findings)
            {
                return new AgentReviewed(prompt.Version, calls, findings);
            }

            last = NotReviewedReason.InvalidOutput;
        }

        return new AgentFailed(prompt.Version, calls, last);
    }

    /// <summary>The standards are sorted and nothing here varies per run, so the prefix can be cached from M2.</summary>
    private static string SystemMessage(Prompt prompt, IReadOnlyList<StandardsDoc> docs)
    {
        var system = new StringBuilder(prompt.Body).AppendLine();
        foreach (var doc in docs.OrderBy(doc => doc.Id, StringComparer.Ordinal))
        {
            system.AppendLine()
                .AppendLine($"<standard id=\"{doc.Id}\" applies_to=\"{string.Join(' ', doc.AppliesTo)}\">")
                .AppendLine(doc.Body)
                .AppendLine("</standard>");
        }

        return system.ToString();
    }

    /// <summary>
    /// Plan text and policy messages come from the PR. Escaping these characters keeps the JSON valid and
    /// means no value can close the fence around it. An agent only sees findings for the standards it was
    /// given, because it couldn't cite any others.
    /// </summary>
    private static string UserMessage(
        CondensedPlan plan, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<Finding> policyFindings, IReadOnlyList<ResourceCostChange>? costs)
    {
        var policies = new JsonArray(
        [
            .. policyFindings
                .Where(finding => docs.Any(doc => doc.Id == finding.Rule))
                .Select(finding => (JsonNode)new JsonObject
                {
                    ["resource_address"] = finding.ResourceAddress,
                    ["doc_id"] = finding.Rule,
                    ["title"] = finding.Title,
                }),
        ]);

        var message = new StringBuilder()
            .AppendLine("<policy_findings>")
            .AppendLine(Escape(CanonicalJson.Write(policies)))
            .AppendLine("</policy_findings>")
            .AppendLine();

        if (costs is not null)
        {
            var figures = new JsonArray(
            [
                .. costs.Where(cost => cost.Priced).Select(cost => (JsonNode)new JsonObject
                {
                    ["resource_address"] = cost.ResourceAddress,
                    ["before_usd"] = cost.BeforeUsd,
                    ["after_usd"] = cost.AfterUsd,
                    ["delta_usd"] = cost.AfterUsd - cost.BeforeUsd,
                }),
            ]);

            message.AppendLine("<cost_data>").AppendLine(Escape(CanonicalJson.Write(figures))).AppendLine("</cost_data>").AppendLine();
        }

        return message.AppendLine("<plan_data>").AppendLine(Escape(plan.ToCanonicalJson())).Append("</plan_data>").ToString();
    }

    private static string Escape(string json) => json
        .Replace("&", "\\u0026", StringComparison.Ordinal)
        .Replace("<", "\\u003c", StringComparison.Ordinal)
        .Replace(">", "\\u003e", StringComparison.Ordinal);

    private static ChatClientAgent NewAgent(IChatClient client, string name, string system, int maxOutputTokens) => new(
        client,
        new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = system,
                MaxOutputTokens = maxOutputTokens,

                // gpt-5.4-mini defaults to none, and without any reasoning it fails valid names on the evals.
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(Schema).RootElement, SchemaName),
                AdditionalProperties = new AdditionalPropertiesDictionary { ["strict"] = true },
            },
        });

    private static AgentCall Call(int attempt, string finish, ChatResponse? response, TimeSpan latency) => new(
        attempt,
        finish,
        response?.Usage?.InputTokenCount ?? 0,
        response?.Usage?.OutputTokenCount ?? 0,
        response?.Usage?.CachedInputTokenCount ?? 0,
        latency);

    private static NotReviewedReason Classify(Exception exception) => exception switch
    {
        ClientResultException { Status: 429 } => NotReviewedReason.RateLimited,
        ClientResultException { Status: 408 } => NotReviewedReason.Timeout,
        ClientResultException { Status: 400 } failure when MentionsContentFilter(failure) => NotReviewedReason.ContentFilter,
        _ => NotReviewedReason.Unavailable,
    };

    // Azure rejects a filtered prompt with a 400 and this code, rather than with a finish reason.
    private static bool MentionsContentFilter(ClientResultException failure)
    {
        try
        {
            return failure.GetRawResponse()?.Content.ToString().Contains("content_filter", StringComparison.Ordinal) == true;
        }
        catch (InvalidOperationException)
        {
            // The body wasn't buffered, so it can't be read.
            return false;
        }
    }

    private static bool IsRefusal(ChatResponse response) =>
        response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>().Any(error => error.ErrorCode == "Refusal");

    /// <summary>The findings in an answer, or null if it doesn't match the wire schema exactly.</summary>
    private Finding[]? ParseFindings(string text, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<ResourceCostChange>? costs)
    {
        WireOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<WireOutput>(text, WireOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (output is null || output.Findings.Any(finding => finding.ResourceAddress.Length == 0 || finding.Title.Trim().Length == 0))
        {
            return null;
        }

        return [.. output.Findings.Select(finding => ToFinding(finding, docs, costs))];
    }

    private Finding ToFinding(WireFinding wire, IReadOnlyList<StandardsDoc> docs, IReadOnlyList<ResourceCostChange>? costs)
    {
        // An unknown document gets a placeholder severity, and the evidence check then drops the finding.
        var severity = docs.FirstOrDefault(doc => doc.Id == wire.DocId)?.Severity ?? Severity.Low;

        // Models like to write tags.environment. Pointers stop at the top-level attribute, as the policies' do.
        var attribute = wire.Attribute.Split(['.', '/', '['], 2)[0].Trim();
        Evidence[] evidence = attribute.Length == 0
            ? [new DocEvidence(wire.DocId)]
            : [new PlanEvidence(CondensedPlan.PointerTo(wire.ResourceAddress, "change", "after", attribute)), new DocEvidence(wire.DocId)];

        return new Finding(wire.ResourceAddress, wire.DocId, severity, profile.Reporter, Deterministic: false, wire.Title.Trim(), evidence)
        {
            Suggestion = string.IsNullOrWhiteSpace(wire.Suggestion) ? null : wire.Suggestion.Trim(),

            // From Infracost, never the model. The evidence check drops a cost finding with no increase here.
            MonthlyCostDeltaUsd = costs?.FirstOrDefault(cost => cost.Priced && cost.ResourceAddress == wire.ResourceAddress) is { } figure
                ? figure.AfterUsd - figure.BeforeUsd
                : null,
        };
    }

    private sealed record WireOutput([property: JsonPropertyName("findings")] IReadOnlyList<WireFinding> Findings);

    private sealed record WireFinding(
        [property: JsonPropertyName("resource_address")] string ResourceAddress,
        [property: JsonPropertyName("doc_id")] string DocId,
        [property: JsonPropertyName("attribute")] string Attribute,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("suggestion")] string? Suggestion);

    /// <summary>Keeps the response, because Agent Framework's own doesn't expose the finish reason.</summary>
    private sealed class ResponseCapture(IChatClient inner) : DelegatingChatClient(inner)
    {
        public ChatResponse? Response { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Response = await base.GetResponseAsync(messages, options, cancellationToken);
            return Response;
        }
    }
}
