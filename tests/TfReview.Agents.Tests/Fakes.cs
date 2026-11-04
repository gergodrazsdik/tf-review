using Microsoft.Extensions.AI;
using TfReview.Scanners;

namespace TfReview.Agents.Tests;

internal sealed record ChatRequest(string User, ChatOptions? Options);

/// <summary>Plays back responses or failures in order, and records what the agent sent.</summary>
internal sealed class FakeChat(params object[] script) : IChatClient
{
    private readonly Queue<object> remaining = new(script);
    private readonly bool hang;

    public FakeChat(bool hang)
        : this()
    {
        this.hang = hang;
    }

    public List<ChatRequest> Requests { get; } = [];

    public static ChatResponse Answer(string text, long input = 1, long output = 1, ChatFinishReason? finish = null) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = finish ?? ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(new ChatRequest(string.Join('\n', messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text)), options));
        if (hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return remaining.Dequeue() switch
        {
            ChatResponse response => response,
            Exception exception => throw exception,
            _ => throw new InvalidOperationException("Bad script."),
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Stands in for conftest: returns fixed output, and keeps the plan it was given.</summary>
internal sealed class FakeProcesses(string output) : IProcessRunner
{
    public string? PlanSeen { get; private set; }

    public async Task<ProcessResult?> RunAsync(
        string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Conftest takes the plan last, and Checkov right after -f.
        PlanSeen = await File.ReadAllTextAsync(arguments[0] == "-f" ? arguments[1] : arguments[^1], cancellationToken);
        return new ProcessResult(1, output);
    }
}

/// <summary>An answer store in memory, keyed by scope and key like the real one.</summary>
internal sealed class MemoryAnswers : IAnswerStore
{
    private readonly Dictionary<(string Scope, string Key), string> answers = [];

    public int Count => this.answers.Count;

    public IEnumerable<string> Keys => this.answers.Keys.Select(entry => entry.Key);

    public Task<string?> ReadAsync(string scope, string key, CancellationToken cancellationToken) =>
        Task.FromResult(this.answers.GetValueOrDefault((scope, key)));

    public Task WriteAsync(string scope, string key, string role, string answer, CancellationToken cancellationToken)
    {
        this.answers[(scope, key)] = answer;
        return Task.CompletedTask;
    }

    /// <summary>Replaces every stored answer, as a wire change would leave them.</summary>
    public void Overwrite(string answer)
    {
        foreach (var entry in this.answers.Keys.ToList())
        {
            this.answers[entry] = answer;
        }
    }
}
