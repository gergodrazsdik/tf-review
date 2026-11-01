using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TfReview.Core;

/// <summary>The redaction layers, so tests can prove each one matters by turning it off.</summary>
[Flags]
internal enum RedactionLayers
{
    None = 0,
    Marks = 1,
    Names = 2,
    Sweep = 4,
    Verifier = 8,
    All = Marks | Names | Sweep | Verifier,
}

/// <summary>
/// Turns the output of <c>tofu show -json</c> into a plan that can leave the plan job (D25).
/// <list type="number">
/// <item>Harvests every value that's marked sensitive or held under a secret-like name, from the whole document.</item>
/// <item>Keeps only allowlisted sections and fields, so anything unexpected is dropped rather than passed on.</item>
/// <item>Replaces marked values, and values under secret-like names, with placeholders.</item>
/// <item>Sweeps harvested values out of everything else, including keys and base64 or URL-encoded forms.</item>
/// <item>Re-scans the result, and refuses the plan if any harvested value survived.</item>
/// </list>
/// Anything malformed refuses the plan. Known gap: an unmarked secret under an ordinary name passes.
/// Running it on its own output changes nothing, so a job that receives a redacted plan can re-run it
/// on what is untrusted input.
/// </summary>
public sealed class PlanRedactor
{
    // Replacing shorter values would corrupt unrelated text, such as a marked "prod" in every name.
    // They're still replaced wherever they're marked or under a secret-like name.
    private const int MinSweepLength = 8;

    private const string ResourceChanges = "resource_changes";
    private const string OutputChanges = "output_changes";
    private const string Change = "change";

    private static readonly Regex Placeholder = new(@"^\[redacted:\d+\]$", RegexOptions.Compiled);

    private static readonly string[] SecretNames =
    [
        "password", "passwd", "pwd", "secret", "token", "credential", "apikey", "accesskey", "accountkey",
        "sharedkey", "privatekey", "primarykey", "secondarykey", "connectionstring", "sasurl",
        "customdata", "userdata", "appsettings", "clientcertificate", "kubeconfig",
    ];

    private static readonly string[] RootFields = ["format_version", ResourceChanges, OutputChanges];

    private static readonly string[] ResourceFields =
    [
        "address", "previous_address", "module_address", "mode", "type", "name", "index", "deposed",
        "provider_name", "action_reason", Change,
    ];

    private static readonly string[] ChangeFields =
    [
        "actions", "before", "after", "after_unknown", "before_sensitive", "after_sensitive", "replace_paths",
    ];

    private readonly RedactionLayers layers;

    public PlanRedactor()
        : this(RedactionLayers.All)
    {
    }

    internal PlanRedactor(RedactionLayers layers) => this.layers = layers;

    public RedactionOutcome Redact(string planJson)
    {
        JsonObject plan;
        try
        {
            if (CanonicalJson.ParseStrict(planJson) is not JsonObject parsed)
            {
                return new Refused(RefusalRule.MalformedPlan, "/");
            }

            plan = parsed;
        }
        catch (JsonException)
        {
            return new Refused(RefusalRule.MalformedJson, "/");
        }

        var format = plan["format_version"] is JsonValue version && version.TryGetValue<string>(out var text) ? text : null;
        if (format is null || !format.StartsWith("1.", StringComparison.Ordinal))
        {
            return new Refused(RefusalRule.UnsupportedFormat, "/format_version");
        }

        if (plan["errored"] is JsonValue errored && errored.TryGetValue<bool>(out var failed) && failed)
        {
            return new Refused(RefusalRule.ErroredPlan, "/errored");
        }

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Harvest(plan, plan, secrets);
            return Build(plan, secrets);
        }
        catch (RefusalException refusal)
        {
            return new Refused(refusal.Rule, refusal.Location);
        }
        catch (InvalidOperationException)
        {
            // A section with the wrong shape, such as an array where an object belongs.
            return new Refused(RefusalRule.MalformedPlan, "/");
        }
    }

    private static bool IsSecretName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SecretNames.Any(secret => normalized.Contains(secret, StringComparison.Ordinal));
    }

    private static bool IsString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            text = value.GetValue<string>();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private void Harvest(JsonObject plan, JsonNode? node, HashSet<string> secrets)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var child in array)
                {
                    Harvest(plan, child, secrets);
                }

                break;
            case JsonObject value:
                HarvestObject(plan, value, secrets);
                foreach (var (key, child) in value)
                {
                    if (layers.HasFlag(RedactionLayers.Names) && IsSecretName(key))
                    {
                        AddStrings(child, secrets);
                    }

                    Harvest(plan, child, secrets);
                }

                break;
        }
    }

    private void HarvestObject(JsonObject plan, JsonObject value, HashSet<string> secrets)
    {
        if (!layers.HasFlag(RedactionLayers.Marks))
        {
            return;
        }

        // A change, in resource_changes, output_changes or resource_drift.
        AddMarked(value["before"], value["before_sensitive"], secrets);
        AddMarked(value["after"], value["after_sensitive"], secrets);

        // A resource in planned_values or prior_state.
        AddMarked(value["values"], value["sensitive_values"], secrets);

        // An output or a variable, in planned_values, prior_state or configuration.
        if (value["sensitive"] is JsonValue flag && flag.TryGetValue<bool>(out var sensitive) && sensitive)
        {
            AddStrings(value["value"], secrets);
            AddStrings(value["default"], secrets);
            AddStrings(value["expression"], secrets);
        }

        // The variables section has no sensitive flag: that's only on its declaration in configuration.
        if (ReferenceEquals(value, plan["variables"]))
        {
            foreach (var (name, variable) in value)
            {
                if (plan["configuration"]?["root_module"]?["variables"]?[name]?["sensitive"] is JsonValue declared
                    && declared.TryGetValue<bool>(out var isSensitive) && isSensitive)
                {
                    AddStrings(variable?["value"], secrets);
                }
            }
        }
    }

    private static void AddMarked(JsonNode? node, JsonNode? mark, HashSet<string> secrets)
    {
        switch (mark)
        {
            case JsonValue value when value.TryGetValue<bool>(out var marked) && marked:
                AddStrings(node, secrets);
                break;
            case JsonObject marks when node is JsonObject children:
                foreach (var (key, child) in marks)
                {
                    AddMarked(children[key], child, secrets);
                }

                break;
            case JsonArray marks when node is JsonArray children:
                for (var i = 0; i < Math.Min(marks.Count, children.Count); i++)
                {
                    AddMarked(children[i], marks[i], secrets);
                }

                break;
        }
    }

    private static void AddStrings(JsonNode? node, HashSet<string> secrets)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (_, child) in value)
                {
                    AddStrings(child, secrets);
                }

                break;
            case JsonArray value:
                foreach (var child in value)
                {
                    AddStrings(child, secrets);
                }

                break;
            default:
                if (IsString(node, out var text) && text.Length > 0 && !Placeholder.IsMatch(text))
                {
                    secrets.Add(text);
                }

                break;
        }
    }

    private RedactionOutcome Build(JsonObject plan, HashSet<string> secrets)
    {
        var dropped = 0;
        var root = new JsonObject { ["format_version"] = plan["format_version"]!.DeepClone() };
        dropped += plan.Count(field => !RootFields.Contains(field.Key, StringComparer.Ordinal));

        var forms = Forms(secrets);
        var changes = new JsonArray();
        if (plan[ResourceChanges] is { } resourceChanges)
        {
            if (resourceChanges is not JsonArray items)
            {
                throw new RefusalException(RefusalRule.MalformedPlan, $"/{ResourceChanges}");
            }

            for (var i = 0; i < items.Count; i++)
            {
                changes.Add(BuildResource(items[i], $"/{ResourceChanges}/{i}", forms, ref dropped));
            }
        }

        root[ResourceChanges] = changes;

        var outputs = new JsonObject();
        if (plan[OutputChanges] is { } outputChanges)
        {
            if (outputChanges is not JsonObject items)
            {
                throw new RefusalException(RefusalRule.MalformedPlan, $"/{OutputChanges}");
            }

            foreach (var (name, change) in items.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                outputs[Sweep(name, forms, new Scope())] = BuildChange(
                    change, $"/{OutputChanges}", forms, new Scope(), ref dropped);
            }
        }

        root[OutputChanges] = outputs;

        if (layers.HasFlag(RedactionLayers.Verifier))
        {
            Verify(root, forms);
        }

        return new Redacted(new RedactedPlan(root), secrets.Count, dropped);
    }

    private JsonObject BuildResource(JsonNode? item, string location, List<SecretForm> forms, ref int dropped)
    {
        if (item is not JsonObject resource || !IsString(resource["address"], out _))
        {
            throw new RefusalException(RefusalRule.MalformedPlan, location);
        }

        var scope = new Scope();
        var result = new JsonObject();
        foreach (var (key, value) in resource.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ResourceFields.Contains(key, StringComparer.Ordinal))
            {
                dropped++;
            }
            else if (key != Change)
            {
                result[key] = SweepTree(value?.DeepClone(), forms, scope);
            }
        }

        result[Change] = BuildChange(resource[Change], location, forms, scope, ref dropped);
        return result;
    }

    private JsonObject BuildChange(JsonNode? item, string location, List<SecretForm> forms, Scope scope, ref int dropped)
    {
        if (item is not JsonObject change || change["actions"] is not JsonArray)
        {
            throw new RefusalException(RefusalRule.MalformedPlan, location);
        }

        dropped += change.Count(field => !ChangeFields.Contains(field.Key, StringComparer.Ordinal));
        var result = new JsonObject { ["actions"] = change["actions"]!.DeepClone() };

        CheckUnknown(change["after_unknown"], location);
        result["after_unknown"] = change["after_unknown"]?.DeepClone();
        if (change["replace_paths"] is { } replacePaths)
        {
            result["replace_paths"] = replacePaths.DeepClone();
        }

        // Before comes first, so a value that didn't change gets the same placeholder on both sides.
        foreach (var (name, marks) in new[] { ("before", "before_sensitive"), ("after", "after_sensitive") })
        {
            var value = change[name];
            var mark = change[marks];
            if (value is not null && mark is null)
            {
                throw new RefusalException(RefusalRule.MalformedMarks, location);
            }

            CheckMarks(mark, location);
            result[name] = RedactValue(value, mark, scope, location);
            result[marks] = mark?.DeepClone();
        }

        return (JsonObject)SweepTree(result, forms, scope)!;
    }

    private JsonNode? RedactValue(JsonNode? value, JsonNode? mark, Scope scope, string location)
    {
        if (value is null)
        {
            return null;
        }

        var marked = mark is JsonValue flag && flag.GetValue<bool>();
        if (marked && layers.HasFlag(RedactionLayers.Marks))
        {
            return scope.Placeholder(value);
        }

        switch (value)
        {
            case JsonObject children:
                if (mark is JsonArray)
                {
                    throw new RefusalException(RefusalRule.MalformedMarks, location);
                }

                var result = new JsonObject();
                foreach (var (key, child) in children.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    result[key] = layers.HasFlag(RedactionLayers.Names) && IsSecretName(key) && IsRedactable(child)
                        ? scope.Placeholder(child!)
                        : RedactValue(child, (mark as JsonObject)?[key], scope, location);
                }

                return result;
            case JsonArray children:
                if (mark is JsonObject || (mark is JsonArray marks && marks.Count != children.Count))
                {
                    throw new RefusalException(RefusalRule.MalformedMarks, location);
                }

                var list = new JsonArray();
                for (var i = 0; i < children.Count; i++)
                {
                    list.Add(RedactValue(children[i], (mark as JsonArray)?[i], scope, location));
                }

                return list;
            default:
                if (mark is JsonObject or JsonArray)
                {
                    throw new RefusalException(RefusalRule.MalformedMarks, location);
                }

                return value.DeepClone();
        }
    }

    // Empty strings stay, because ADR-002 tests for them, and booleans carry no secret.
    private static bool IsRedactable(JsonNode? value) => value switch
    {
        null => false,
        JsonValue scalar when scalar.GetValueKind() is JsonValueKind.True or JsonValueKind.False => false,
        JsonValue scalar when IsString(scalar, out var text) && text.Length == 0 => false,
        _ => true,
    };

    private JsonNode? SweepTree(JsonNode? node, List<SecretForm> forms, Scope scope)
    {
        switch (node)
        {
            case JsonObject value:
                var result = new JsonObject();
                foreach (var (key, child) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var swept = Sweep(key, forms, scope);
                    if (result.ContainsKey(swept))
                    {
                        throw new RefusalException(RefusalRule.MalformedPlan, "/");
                    }

                    result[swept] = SweepTree(child?.DeepClone(), forms, scope);
                }

                return result;
            case JsonArray value:
                var list = new JsonArray();
                foreach (var child in value)
                {
                    list.Add(SweepTree(child?.DeepClone(), forms, scope));
                }

                return list;
            default:
                return IsString(node, out var text) ? JsonValue.Create(Sweep(text, forms, scope)) : node;
        }
    }

    private string Sweep(string text, List<SecretForm> forms, Scope scope)
    {
        if (!layers.HasFlag(RedactionLayers.Sweep))
        {
            return text;
        }

        foreach (var form in forms)
        {
            if (text.Contains(form.Text, StringComparison.Ordinal))
            {
                text = text.Replace(form.Text, scope.PlaceholderFor(form.Secret), StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static List<SecretForm> Forms(HashSet<string> secrets)
    {
        var forms = new List<SecretForm>();
        foreach (var secret in secrets.Where(secret => secret.Length >= MinSweepLength))
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
            forms.Add(new SecretForm(secret, secret));
            forms.Add(new SecretForm(secret, base64));
            forms.Add(new SecretForm(secret, base64.TrimEnd('=')));
            forms.Add(new SecretForm(secret, base64.TrimEnd('=').Replace('+', '-').Replace('/', '_')));
            forms.Add(new SecretForm(secret, Uri.EscapeDataString(secret)));
        }

        // Longest first, so a secret containing another isn't left half replaced. Then ordinal, to stay deterministic.
        return [.. forms
            .DistinctBy(form => form.Text)
            .OrderByDescending(form => form.Text.Length)
            .ThenBy(form => form.Text, StringComparer.Ordinal)];
    }

    private static void Verify(JsonNode? node, List<SecretForm> forms)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (key, child) in value)
                {
                    VerifyText(key, forms);
                    Verify(child, forms);
                }

                break;
            case JsonArray value:
                foreach (var child in value)
                {
                    Verify(child, forms);
                }

                break;
            default:
                if (IsString(node, out var text))
                {
                    VerifyText(text, forms);
                }

                break;
        }
    }

    private static void VerifyText(string text, List<SecretForm> forms)
    {
        if (forms.Any(form => text.Contains(form.Text, StringComparison.Ordinal)))
        {
            throw new RefusalException(RefusalRule.SecretSurvived, "/");
        }
    }

    // Marks hold only booleans. Anything else could be a value, and would pass straight through.
    private static void CheckMarks(JsonNode? mark, string location)
    {
        if (!OnlyBooleans(mark))
        {
            throw new RefusalException(RefusalRule.MalformedMarks, location);
        }
    }

    private static void CheckUnknown(JsonNode? unknown, string location)
    {
        if (!OnlyBooleans(unknown))
        {
            throw new RefusalException(RefusalRule.MalformedUnknown, location);
        }
    }

    private static bool OnlyBooleans(JsonNode? node) => node switch
    {
        null => true,
        JsonObject value => value.All(field => OnlyBooleans(field.Value)),
        JsonArray value => value.All(OnlyBooleans),
        JsonValue value => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    private sealed record SecretForm(string Secret, string Text);

    private sealed class RefusalException(RefusalRule rule, string location) : Exception
    {
        public RefusalRule Rule { get; } = rule;

        public string Location { get; } = location;
    }

    /// <summary>
    /// Numbers placeholders per resource, by distinct value. Nothing is derived from the secret, the same
    /// value stays recognizable within a resource, and adding a resource doesn't change another's bytes.
    /// </summary>
    private sealed class Scope
    {
        private readonly Dictionary<string, int> ordinals = new(StringComparer.Ordinal);

        public JsonNode Placeholder(JsonNode value)
        {
            // Already a placeholder: keep its text, so redacting a redacted plan changes nothing.
            if (IsString(value, out var text) && PlanRedactor.Placeholder.IsMatch(text))
            {
                ordinals.TryAdd(text, ordinals.Count + 1);
                return JsonValue.Create(text)!;
            }

            return JsonValue.Create(PlaceholderFor(IsString(value, out text) ? text : CanonicalJson.Write(value)))!;
        }

        public string PlaceholderFor(string value)
        {
            if (!ordinals.TryGetValue(value, out var ordinal))
            {
                ordinal = ordinals.Count + 1;
                ordinals[value] = ordinal;
            }

            return $"[redacted:{ordinal}]";
        }
    }
}
