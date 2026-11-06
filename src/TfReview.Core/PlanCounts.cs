using System.Text.Json.Nodes;

namespace TfReview.Core;

/// <summary>How many resources a plan changes, by action, for the PR comment.</summary>
public sealed record PlanCounts(int ResourceChanges, IReadOnlyDictionary<string, int> Actions)
{
    private const string NoOp = "no-op";

    public static PlanCounts From(RedactedPlan plan)
    {
        var actions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var change in plan.Root["resource_changes"]!.AsArray())
        {
            // A data source read is no change to a resource (BUG-26).
            if (change!["mode"]?.GetValue<string>() == "data")
            {
                continue;
            }

            var action = Classify(change!["change"]!["actions"]!.AsArray());
            actions[action] = actions.GetValueOrDefault(action) + 1;
        }

        return new PlanCounts(actions.Where(action => action.Key != NoOp).Sum(action => action.Value), actions);
    }

    // Only fixed names come out of here, so the result is safe to render.
    private static string Classify(JsonArray actions)
    {
        var names = actions.Select(action => action?.GetValue<string>()).ToArray();
        return names switch
        {
            ["create"] => "create",
            ["update"] => "update",
            ["delete"] => "delete",
            ["read"] => "read",
            [NoOp] => NoOp,
            ["delete", "create"] or ["create", "delete"] => "replace",
            _ => "other",
        };
    }
}
