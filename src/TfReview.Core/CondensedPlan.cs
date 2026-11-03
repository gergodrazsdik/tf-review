using System.Text.Json.Nodes;

namespace TfReview.Core;

/// <summary>One change: where it is, what kind of resource, and what the plan does to it.</summary>
public sealed record PlanChange(string Address, string Type, IReadOnlyList<string> Actions);

/// <summary>
/// What the agents see: only real changes, keyed by address, with unchanged attributes removed. Findings
/// point into this form, and keying by address keeps a pointer valid across slices and re-pushes. Agents
/// take this type, which can only come from a <see cref="RedactedPlan"/>.
/// </summary>
public sealed class CondensedPlan
{
    private const string ResourceChanges = "resource_changes";
    private const string Change = "change";
    private const string After = "after";
    private const string Before = "before";
    private const string AfterUnknown = "after_unknown";

    // Always kept. Without tags, updating one tag's value looks like a resource with no other tags.
    private static readonly string[] AlwaysKept = ["name", "tags"];

    private static readonly string[] ResourceFields = ["type", "mode", "index", "deposed", "previous_address", "action_reason"];

    private readonly JsonObject root;

    private CondensedPlan(JsonObject root) => this.root = root;

    public static CondensedPlan From(RedactedPlan plan)
    {
        var resources = new JsonObject();
        var changes = plan.Root[ResourceChanges]!.AsArray()
            .Select(change => change!.AsObject())
            .Where(change => !IsNoOp(change))
            .OrderBy(Key, StringComparer.Ordinal);

        foreach (var change in changes)
        {
            resources[Key(change)] = Condense(change);
        }

        return new CondensedPlan(new JsonObject { [ResourceChanges] = resources });
    }

    /// <summary>The JSON Pointer to an attribute of a resource's change, such as its <c>after</c> tags.</summary>
    public static string PointerTo(string address, params string[] path) =>
        string.Join('/', new[] { string.Empty, ResourceChanges, Escape(address) }.Concat(path.Select(Escape)));

    /// <summary>The resource a pointer belongs to, or null if it doesn't point at one.</summary>
    public static string? AddressOf(string pointer)
    {
        var parts = pointer.Split('/');
        return parts is ["", ResourceChanges, var address, ..] ? Unescape(address) : null;
    }

    /// <summary>Whether a pointer reaches something in this plan. A null value counts: it's evidence too.</summary>
    public bool Resolves(string pointer)
    {
        if (!pointer.StartsWith('/'))
        {
            return false;
        }

        JsonNode? current = root;
        foreach (var part in pointer.Split('/').Skip(1).Select(Unescape))
        {
            switch (current)
            {
                case JsonObject value when value.ContainsKey(part):
                    current = value[part];
                    break;
                case JsonArray value when int.TryParse(part, out var index) && index >= 0 && index < value.Count:
                    current = value[index];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a pointer names an attribute an update leaves as it was. Those stay in the condensed plan
    /// for context, such as a resource's name and tags, but this plan isn't what put their values there.
    /// </summary>
    public bool PointsAtUnchangedAttribute(string pointer)
    {
        var parts = pointer.Split('/').Select(Unescape).ToArray();
        if (parts is not ["", ResourceChanges, var address, Change, After, var attribute]
            || root[ResourceChanges]?[address]?[Change] is not JsonObject change
            || change[Before] is not JsonObject before
            || change[After] is not JsonObject after
            || !before.ContainsKey(attribute)
            || !after.ContainsKey(attribute))
        {
            return false;
        }

        return CanonicalJson.Write(before[attribute]) == CanonicalJson.Write(after[attribute]);
    }

    /// <summary>A plan holding only the resources whose type passes, so an agent sees what its standards cover.</summary>
    public CondensedPlan OnlyTypes(Func<string, bool> keep) => new(new JsonObject
    {
        [ResourceChanges] = new JsonObject(
            root[ResourceChanges]!.AsObject()
                .Where(resource => resource.Value?["type"]?.GetValue<string>() is { } type && keep(type))
                .Select(resource => KeyValuePair.Create(resource.Key, resource.Value?.DeepClone()))),
    });

    /// <summary>The changes in this plan, sorted by address, without any of their values.</summary>
    public IReadOnlyList<PlanChange> Changes() =>
    [
        .. root[ResourceChanges]!.AsObject().Select(resource => new PlanChange(
            resource.Key,
            resource.Value?["type"]?.GetValue<string>() ?? string.Empty,
            [.. resource.Value![Change]!["actions"]!.AsArray().Select(action => action!.GetValue<string>())])),
    ];

    /// <summary>The resource type at an address, or null if the plan has no such change.</summary>
    public string? TypeOf(string address) =>
        root[ResourceChanges]!.AsObject().TryGetPropertyValue(address, out var resource)
            ? resource?["type"]?.GetValue<string>()
            : null;

    /// <summary>The distinct resource types that change, which decide the standards an agent retrieves.</summary>
    public IReadOnlyList<string> ResourceTypes() =>
    [
        .. root[ResourceChanges]!.AsObject()
            .Select(resource => resource.Value?["type"]?.GetValue<string>())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The names of the attributes that change, without their values, which say what a slice is about to a search.
    /// Name and tags are always kept, so they only count when they changed.
    /// </summary>
    public IReadOnlyList<string> ChangedAttributeNames() =>
    [
        .. root[ResourceChanges]!.AsObject()
            .SelectMany(resource => resource.Value?[Change]?[After] is JsonObject after ? after.Select(attribute => (resource.Key, attribute.Key)) : [])
            .Where(item => !AlwaysKept.Contains(item.Item2, StringComparer.Ordinal)
                || !this.PointsAtUnchangedAttribute(PointerTo(item.Item1, Change, After, item.Item2)))
            .Select(item => item.Item2)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    public string ToCanonicalJson() => CanonicalJson.Write(root);

    private static bool IsNoOp(JsonObject change) =>
        change[Change]!["actions"]!.AsArray().Select(action => action!.GetValue<string>()).SequenceEqual(["no-op"]);

    private static string Key(JsonObject change) => RedactedPlan.ResourceKey(change);

    private static JsonObject Condense(JsonObject resource)
    {
        var change = resource[Change]!.AsObject();
        var result = new JsonObject();
        foreach (var field in ResourceFields)
        {
            if (resource.TryGetPropertyValue(field, out var value))
            {
                result[field] = value?.DeepClone();
            }
        }

        var before = change[Before] as JsonObject;
        var after = change[After] as JsonObject;
        var unknown = change[AfterUnknown] as JsonObject;
        var condensed = new JsonObject { ["actions"] = change["actions"]!.DeepClone() };
        if (change["replace_paths"] is { } replacePaths)
        {
            condensed["replace_paths"] = replacePaths.DeepClone();
        }

        // Before and after are only compared when both are objects. On a create or a delete one side is
        // null, so everything on the other side stays, including the nulls that show a missing attribute.
        if (before is not null && after is not null)
        {
            var kept = KeptAttributes(before, after, unknown);
            condensed[Before] = Select(before, kept);
            condensed[After] = Select(after, kept);
            condensed[AfterUnknown] = unknown is null ? null : Select(unknown, kept);
        }
        else
        {
            condensed[Before] = change[Before]?.DeepClone();
            condensed[After] = change[After]?.DeepClone();
            condensed[AfterUnknown] = change[AfterUnknown]?.DeepClone();
        }

        result[Change] = condensed;
        return result;
    }

    private static HashSet<string> KeptAttributes(JsonObject before, JsonObject after, JsonObject? unknown)
    {
        var names = before.Select(field => field.Key).Union(after.Select(field => field.Key));
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var unchanged = before.ContainsKey(name) == after.ContainsKey(name)
                && CanonicalJson.Write(before[name]) == CanonicalJson.Write(after[name]);
            if (!unchanged || AlwaysKept.Contains(name, StringComparer.Ordinal))
            {
                kept.Add(name);
            }
        }

        // A known value becoming unknown is a change, even though it's only visible here.
        foreach (var (name, value) in unknown ?? [])
        {
            if (ContainsTrue(value))
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    private static JsonObject Select(JsonObject value, HashSet<string> names) =>
        new(value.Where(field => names.Contains(field.Key))
            .Select(field => KeyValuePair.Create(field.Key, field.Value?.DeepClone())));

    private static bool ContainsTrue(JsonNode? node) => node switch
    {
        JsonObject value => value.Any(field => ContainsTrue(field.Value)),
        JsonArray value => value.Any(ContainsTrue),
        JsonValue value => value.TryGetValue<bool>(out var flag) && flag,
        _ => false,
    };

    private static string Escape(string part) =>
        part.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string part) =>
        part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
