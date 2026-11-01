using System.Text.Json.Nodes;

namespace TfReview.Core;

public enum PlanSide
{
    Before,
    After,
}

/// <summary>
/// A plan scanners can read, rebuilt from a <see cref="RedactedPlan"/> (D32). Infracost and Checkov need
/// <c>planned_values</c>, which redaction removes, and on a plan without it they exit 0 with an empty result
/// that looks like a clean scan. Each side is a plan that creates that side's resources, so scanning both
/// gives the cost before and after with no baseline to store (D31).
/// </summary>
public sealed class ScannablePlan
{
    private const string NoOp = "no-op";

    private static readonly string[] IdentityFields = ["address", "mode", "type", "name", "index", "provider_name"];

    private readonly JsonObject root;

    private ScannablePlan(JsonObject root, IReadOnlyList<string> addresses)
    {
        this.root = root;
        Addresses = addresses;
    }

    /// <summary>The resources on this side. A scanner reporting none when this isn't empty didn't really run.</summary>
    public IReadOnlyList<string> Addresses { get; }

    public static ScannablePlan From(RedactedPlan plan, PlanSide side)
    {
        var name = side == PlanSide.Before ? "before" : "after";
        var resources = new JsonArray();
        var changes = new JsonArray();
        var addresses = new List<string>();

        foreach (var change in plan.Root["resource_changes"]!.AsArray().Select(item => item!.AsObject()))
        {
            if (change["change"]![name] is not JsonObject values || !IsScannable(change))
            {
                continue;
            }

            addresses.Add(change["address"]!.GetValue<string>());

            var resource = Identity(change);
            resource["schema_version"] = 0;
            resource["values"] = values.DeepClone();
            resource["sensitive_values"] = new JsonObject();
            resources.Add(resource);

            var created = Identity(change);
            created["change"] = new JsonObject
            {
                ["actions"] = new JsonArray("create"),
                ["before"] = null,
                ["after"] = values.DeepClone(),
                ["after_unknown"] = new JsonObject(),
                ["before_sensitive"] = false,
                ["after_sensitive"] = new JsonObject(),
            };
            changes.Add(created);
        }

        return new ScannablePlan(
            new JsonObject
            {
                ["format_version"] = plan.Root["format_version"]!.DeepClone(),
                ["planned_values"] = new JsonObject { ["root_module"] = new JsonObject { ["resources"] = resources } },
                ["resource_changes"] = changes,
            },
            addresses);
    }

    public string ToCanonicalJson() => CanonicalJson.Write(root);

    // A deposed instance is on its way out and shares its address with the live one. A data source costs
    // nothing, and a no-op doesn't change what the plan costs.
    private static bool IsScannable(JsonObject change) =>
        change["deposed"] is null
        && change["mode"]?.GetValue<string>() != "data"
        && !change["change"]!["actions"]!.AsArray().Select(action => action!.GetValue<string>()).SequenceEqual([NoOp]);

    private static JsonObject Identity(JsonObject change)
    {
        var identity = new JsonObject();
        foreach (var field in IdentityFields)
        {
            if (change.TryGetPropertyValue(field, out var value))
            {
                identity[field] = value?.DeepClone();
            }
        }

        return identity;
    }
}
