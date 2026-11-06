using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TfReview.Agents;

/// <summary>Where model answers are kept between reviews, one partition per repo (D53).</summary>
public interface IAnswerStore
{
    /// <summary>The answer stored under this key in this repo's partition, or null.</summary>
    Task<string?> ReadAsync(string scope, string key, CancellationToken cancellationToken);

    Task WriteAsync(string scope, string key, string role, string answer, CancellationToken cancellationToken);
}

/// <summary>
/// The result cache (D53): a model call whose input is exactly the same as one already answered for this repo gets that
/// answer back, without calling the model. The key covers everything the model is given, so a changed resource, standard,
/// reference, prompt version, deployment or model version is a new call. Only an answer that parsed is kept, and the cache
/// can only ever cost a call: a store that fails or is slow is a miss.
/// </summary>
/// <param name="inner">The model.</param>
/// <param name="store">Where answers are kept.</param>
/// <param name="scope">The repo's ID, which partitions the store.</param>
/// <param name="deployment">The model deployment, with its version when known, which is part of the key.</param>
/// <param name="log">Receives a line when the store fails, holding no prompt or answer text.</param>
public sealed class AnswerCache(IChatClient inner, IAnswerStore store, string scope, string deployment, Action<string>? log = null)
    : DelegatingChatClient(inner)
{
    /// <summary>The finish reported for a call answered from the cache, which tags its metrics.</summary>
    public const string Finish = "cached";

    /// <summary>
    /// The call options StructuredCall sets in code rather than in the prompt: output limit, reasoning effort, strict
    /// output. Change it with them, so answers given under the old options aren't reused.
    /// </summary>
    private const string CallShape = "v1";

    private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A hash of the call's whole input, so the store never holds the prompt.</summary>
    public string KeyFor(string role, string system, string user, string schema)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(new[] { CallShape, deployment, role, system, user, schema });
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StoreTimeout);
            return await store.ReadAsync(scope, key, deadline.Token);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"answer cache: read failed, {e.GetType().Name}.");
            return null;
        }
    }

    public async Task WriteAsync(string key, string role, string answer, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StoreTimeout);
            await store.WriteAsync(scope, key, role, answer, deadline.Token);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"answer cache: write failed, {e.GetType().Name}.");
        }
    }

    /// <summary>The call reported for an answer from the cache: no tokens, attempt 0 because the model wasn't asked.</summary>
    internal static AgentCall Hit(TimeSpan latency) => new(0, Finish, 0, 0, 0, latency);
}
