using System.Text.Json;
using System.Text.Json.Serialization;

namespace TfReview.Core;

/// <summary>
/// How many resources a plan changes, by action. It's all the walking skeleton lets out of the
/// plan job, so no plan content can leak before real redaction exists. The plan job runs PR code,
/// so anything reading this file treats it as untrusted: <see cref="Parse"/> only accepts known
/// action names, which makes it safe to render.
/// </summary>
public sealed record PlanCounts(
    [property: JsonPropertyName("resource_changes")] int ResourceChanges,
    [property: JsonPropertyName("actions")] IReadOnlyDictionary<string, int> Actions)
{
    private const string Replace = "replace";
    private const string Other = "other";

    private static readonly string[] KnownActions =
        ["create", "update", "delete", Replace, "read", "no-op", Other];

    /// <summary>Counts the changes in the output of <c>tofu show -json</c>.</summary>
    public static PlanCounts FromPlan(JsonDocument plan)
    {
        if (!plan.RootElement.TryGetProperty("resource_changes", out var changes))
        {
            return new PlanCounts(0, new Dictionary<string, int>());
        }

        var actions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var change in changes.EnumerateArray())
        {
            total++;
            var action = Classify(change.GetProperty("change").GetProperty("actions"));
            actions[action] = actions.GetValueOrDefault(action) + 1;
        }

        return new PlanCounts(total, actions);
    }

    /// <summary>Reads a counts file, and throws <see cref="JsonException"/> on anything unexpected.</summary>
    public static PlanCounts Parse(string json)
    {
        var counts = JsonSerializer.Deserialize<PlanCounts>(json)
            ?? throw new JsonException("The counts file is empty.");

        if (counts.ResourceChanges < 0 || counts.Actions is null)
        {
            throw new JsonException("The counts file is malformed.");
        }

        foreach (var (action, count) in counts.Actions)
        {
            if (!KnownActions.Contains(action, StringComparer.Ordinal) || count < 0)
            {
                throw new JsonException("The counts file has an unknown action or a negative count.");
            }
        }

        return counts;
    }

    private static string Classify(JsonElement actions)
    {
        var names = actions.EnumerateArray().Select(action => action.GetString()).ToArray();
        return names switch
        {
            ["create"] => "create",
            ["update"] => "update",
            ["delete"] => "delete",
            ["read"] => "read",
            ["no-op"] => "no-op",
            ["delete", "create"] or ["create", "delete"] => Replace,
            _ => Other,
        };
    }
}
