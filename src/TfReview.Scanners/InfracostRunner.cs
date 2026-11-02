using System.Globalization;
using System.Text.Json;
using TfReview.Core;

namespace TfReview.Scanners;

/// <summary>Why Infracost's result can't be trusted. Each one means the plan's cost is unknown, not zero.</summary>
public enum InfracostFailure
{
    CouldNotRun,
    NoJson,
    NoResources,
    MalformedResult,
}

/// <summary>
/// What one resource costs a month, in US dollars. A side with no resource is 0, and a resource Infracost
/// can't price has <see cref="Priced"/> false, because reporting it as free would be a false statement.
/// </summary>
public sealed record ResourceCost(string Address, decimal Before, decimal After, bool Priced)
{
    public decimal Delta => After - Before;
}

public closed record InfracostOutcome;

public sealed record InfracostRan(IReadOnlyList<ResourceCost> Costs) : InfracostOutcome
{
    /// <summary>The monthly change across what could be priced. Check <see cref="Costs"/> for what couldn't.</summary>
    public decimal TotalDelta => Costs.Where(cost => cost.Priced).Sum(cost => cost.Delta);
}

public sealed record InfracostFailed(InfracostFailure Reason) : InfracostOutcome;

/// <summary>
/// Prices each side of a plan and works out the change per resource (D31). It doesn't use
/// <c>infracost diff</c>, which matches by file path and reports two files as a project removed and another
/// added. Only cost fields are read: Infracost also applies an org tagging policy, which isn't our standards.
/// </summary>
public sealed class InfracostRunner(IProcessRunner processes, string program = "infracost")
{
    /// <summary>What a scan needs passed on: its login, its pinned plugins, and no update or agent checks.</summary>
    public static readonly string[] EnvironmentVariables =
    [
        "INFRACOST_CLI_AUTHENTICATION_TOKEN", "INFRACOST_CLI_PLUGIN_DIR", "INFRACOST_SKIP_UPDATE_CHECK", "INFRACOST_SKIP_AGENT_CHECK",
    ];

    // Without a token a scan waits forever for a login, so this also ends that.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public async Task<InfracostOutcome> RunAsync(RedactedPlan plan, CancellationToken cancellationToken = default)
    {
        var before = ScannablePlan.From(plan, PlanSide.Before);
        var after = ScannablePlan.From(plan, PlanSide.After);

        var directory = Directory.CreateTempSubdirectory("tfreview-");
        try
        {
            // A side with no resources isn't scanned: pricing runs are limited, and it would cost nothing.
            var beforeScan = await ScanAsync(before, Path.Combine(directory.FullName, "before.json"), cancellationToken);
            if (beforeScan.Failure is { } beforeFailure)
            {
                return new InfracostFailed(beforeFailure);
            }

            var afterScan = await ScanAsync(after, Path.Combine(directory.FullName, "after.json"), cancellationToken);
            if (afterScan.Failure is { } afterFailure)
            {
                return new InfracostFailed(afterFailure);
            }

            return new InfracostRan(Combine(before, after, beforeScan.Prices!, afterScan.Prices!));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Reads one scan: what each resource costs, or null for one Infracost doesn't support.</summary>
    public static ScanResult ParseScan(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return new ScanResult(null, InfracostFailure.NoJson);
        }

        using (document)
        {
            try
            {
                var prices = new Dictionary<string, decimal?>(StringComparer.Ordinal);
                foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
                {
                    foreach (var resource in project.GetProperty("resources").EnumerateArray())
                    {
                        var address = resource.GetProperty("name").GetString() ?? throw new JsonException();
                        prices[address] = resource.GetProperty("is_supported").GetBoolean() ? MonthlyCost(resource) : null;
                    }
                }

                return new ScanResult(prices, null);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException or FormatException or OverflowException)
            {
                return new ScanResult(null, InfracostFailure.MalformedResult);
            }
        }
    }

    private static List<ResourceCost> Combine(
        ScannablePlan before, ScannablePlan after, IReadOnlyDictionary<string, decimal?> beforePrices, IReadOnlyDictionary<string, decimal?> afterPrices)
    {
        return
        [
            .. before.Addresses.Union(after.Addresses, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(address =>
                {
                    var (beforeCost, beforePriced) = Price(address, before, beforePrices);
                    var (afterCost, afterPriced) = Price(address, after, afterPrices);
                    return new ResourceCost(address, beforeCost, afterCost, beforePriced && afterPriced);
                }),
        ];
    }

    private static (decimal Cost, bool Priced) Price(string address, ScannablePlan side, IReadOnlyDictionary<string, decimal?> prices)
    {
        // Not on this side at all, as with one side of a create or a delete: it costs nothing there.
        if (!side.Addresses.Contains(address, StringComparer.Ordinal))
        {
            return (0, true);
        }

        return prices.TryGetValue(address, out var price) && price is { } cost ? (cost, true) : (0, false);
    }

    private static decimal MonthlyCost(JsonElement resource)
    {
        var total = 0m;
        if (resource.TryGetProperty("cost_components", out var components))
        {
            foreach (var component in components.EnumerateArray())
            {
                total += decimal.Parse(component.GetProperty("total_monthly_cost").GetString() ?? throw new JsonException(), CultureInfo.InvariantCulture);
            }
        }

        if (resource.TryGetProperty("subresources", out var subresources))
        {
            foreach (var subresource in subresources.EnumerateArray())
            {
                total += MonthlyCost(subresource);
            }
        }

        return total;
    }

    private async Task<ScanResult> ScanAsync(ScannablePlan plan, string path, CancellationToken cancellationToken)
    {
        if (plan.Addresses.Count == 0)
        {
            return new ScanResult(new Dictionary<string, decimal?>(), null);
        }

        await File.WriteAllTextAsync(path, plan.ToCanonicalJson(), cancellationToken);
        var result = await processes.RunAsync(
            program, ["scan", path, "--json", "--no-color", "--currency", "USD"], Timeout, cancellationToken);
        if (result is null)
        {
            return new ScanResult(null, InfracostFailure.CouldNotRun);
        }

        var scan = ParseScan(result.Output);

        // The false clean: it read nothing, exited 0, and reported a total of $0.
        return scan.Prices is { Count: 0 } ? new ScanResult(null, InfracostFailure.NoResources) : scan;
    }
}

/// <summary>One scan's prices by address, with null for a type Infracost doesn't support, or why it failed.</summary>
public sealed record ScanResult(IReadOnlyDictionary<string, decimal?>? Prices, InfracostFailure? Failure);
