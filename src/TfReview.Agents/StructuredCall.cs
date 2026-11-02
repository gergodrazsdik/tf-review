using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TfReview.Core;

namespace TfReview.Agents;

/// <summary>What a model call came to: a parsed answer, or why there's none. Calls are always reported.</summary>
internal sealed record StructuredResult<T>(T? Value, NotReviewedReason? Failure, IReadOnlyList<AgentCall> Calls)
    where T : class;

/// <summary>
/// One structured model call, with the rules every LLM role follows: no tools, strict output, one retry for
/// invalid or cut-off answers, and each other failure reported rather than guessed around.
/// </summary>
internal static class StructuredCall
{
    public const int MaxOutputTokens = 4_000;

    public static readonly JsonSerializerOptions WireOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private const int MaxAttempts = 2;

    public static async Task<StructuredResult<T>> RunAsync<T>(
        IChatClient chat,
        string name,
        string system,
        string user,
        string schema,
        Func<string, T?> parse,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where T : class
    {
        var calls = new List<AgentCall>();
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
                await NewAgent(capture, name, system, schema, maxOutputTokens).RunAsync(user, cancellationToken: deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                calls.Add(Call(attempt, "timeout", null, stopwatch.Elapsed));
                return new StructuredResult<T>(null, NotReviewedReason.Timeout, calls);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The SDK has already retried what's transient. Any failure here has to become a "not
                // reviewed" for this commit, and only the type is logged, as messages can quote the request.
                calls.Add(Call(attempt, e.GetType().Name, null, stopwatch.Elapsed));
                return new StructuredResult<T>(null, Classify(e), calls);
            }

            var response = capture.Response;
            calls.Add(Call(attempt, response?.FinishReason?.ToString() ?? "unknown", response, stopwatch.Elapsed));

            if (response?.FinishReason == ChatFinishReason.ContentFilter)
            {
                return new StructuredResult<T>(null, NotReviewedReason.ContentFilter, calls);
            }

            if (response is not null && IsRefusal(response))
            {
                return new StructuredResult<T>(null, NotReviewedReason.Refused, calls);
            }

            // The same limit would cut it off again, so the one retry gets more room.
            if (response?.FinishReason == ChatFinishReason.Length)
            {
                last = NotReviewedReason.Truncated;
                maxOutputTokens *= 2;
                continue;
            }

            if (response is not null && parse(response.Text) is { } value)
            {
                return new StructuredResult<T>(value, null, calls);
            }

            last = NotReviewedReason.InvalidOutput;
        }

        return new StructuredResult<T>(null, last, calls);
    }

    /// <summary>
    /// Text from a PR, as JSON. Escaping these characters keeps the JSON valid and means no value can close
    /// the fence around it.
    /// </summary>
    public static string Escape(string json) => json
        .Replace("&", "\\u0026", StringComparison.Ordinal)
        .Replace("<", "\\u003c", StringComparison.Ordinal)
        .Replace(">", "\\u003e", StringComparison.Ordinal);

    /// <summary>Reads an answer against its wire record, or returns null if it doesn't match exactly.</summary>
    public static T? ParseWire<T>(string text)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, WireOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatClientAgent NewAgent(IChatClient client, string name, string system, string schema, int maxOutputTokens) => new(
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
                ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(schema).RootElement, $"{name}_output"),
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
