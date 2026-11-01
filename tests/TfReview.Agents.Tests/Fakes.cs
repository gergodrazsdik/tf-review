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
