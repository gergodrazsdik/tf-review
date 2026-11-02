using System.Text.Json;
using TfReview.Core;

namespace TfReview.Scanners;

/// <summary>Why Checkov's result can't be trusted. Each one means the plan wasn't checked.</summary>
public enum CheckovFailure
{
    CouldNotRun,
    NoJson,
    CanaryNotSeen,
    MalformedResult,
}

public closed record CheckovOutcome;

/// <summary>The failures on the allowlist, sorted so the bytes are stable.</summary>
public sealed record CheckovRan(IReadOnlyList<CheckovViolation> Violations) : CheckovOutcome
{
    public IReadOnlyList<Finding> ToFindings(CondensedPlan plan) =>
    [
        .. Violations.Select(violation => new Finding(
            violation.Address,
            violation.CheckId,
            violation.Severity,
            Reporter.Scanner,
            Deterministic: true,
            violation.Title,
            [new PlanEvidence(Pointer(plan, violation))])),
    ];

    // An attribute can be absent, for example when it's missing altogether, which is what a check can fail on.
    private static string Pointer(CondensedPlan plan, CheckovViolation violation)
    {
        if (violation.Attribute is { } attribute)
        {
            var pointer = CondensedPlan.PointerTo(violation.Address, "change", "after", attribute);
            if (plan.Resolves(pointer))
            {
                return pointer;
            }
        }

        return CondensedPlan.PointerTo(violation.Address, "change", "after");
    }
}

public sealed record CheckovViolation(string CheckId, string Address, Severity Severity, string Title, string? Attribute);

public sealed record CheckovFailed(CheckovFailure Reason) : CheckovOutcome;

/// <summary>
/// Runs Checkov over the planned side of a plan. Its exit code and an empty result both prove nothing: it
/// ignores a plan it doesn't recognize and exits 0, and a plan of types it has no checks for looks the same.
/// So every scan includes a canary that always fails, and a result without it means Checkov didn't really
/// read the plan (D32). Only allowlisted checks become findings, with severity set here (D34), because it
/// fails many checks per resource, and those looking for related resources can't pass on a rebuilt plan.
/// </summary>
public sealed class CheckovRunner(IProcessRunner processes, string program = "checkov")
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    // Checks worth a row, in words that say what's wrong, since Checkov's own titles read like advice ("Ensure
    // that..."). Checks that would need `configuration` don't belong here.
    private static readonly Dictionary<string, (Severity Severity, string Title)> Allowlist = new(StringComparer.Ordinal)
    {
        ["CKV_AZURE_9"] = (Severity.High, "RDP is open to the internet."),
        ["CKV_AZURE_10"] = (Severity.High, "SSH is open to the internet."),
        ["CKV_AZURE_44"] = (Severity.Medium, "The storage account allows an old TLS version."),
        ["CKV_AZURE_59"] = (Severity.High, "The storage account allows public network access."),
        ["CKV_AZURE_190"] = (Severity.High, "The storage account allows public access to blobs."),
    };

    public async Task<CheckovOutcome> RunAsync(RedactedPlan plan, CancellationToken cancellationToken = default)
    {
        var after = ScannablePlan.From(plan, PlanSide.After);

        // Nothing is left after this plan, such as when it only deletes, so there's nothing to check.
        if (after.Addresses.Count == 0)
        {
            return new CheckovRan([]);
        }

        var directory = Directory.CreateTempSubdirectory("tfreview-");
        try
        {
            var path = Path.Combine(directory.FullName, "plan.json");
            await File.WriteAllTextAsync(path, after.ToCanonicalJsonWithCanary(), cancellationToken);

            var result = await processes.RunAsync(
                program,
                ["-f", path, "--framework", "terraform_plan", "--skip-download", "--quiet", "--compact", "--output", "json"],
                Timeout,
                cancellationToken);

            return result is null ? new CheckovFailed(CheckovFailure.CouldNotRun) : Parse(result.Output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Reads Checkov's output. Only its contents count, because exit 1 means checks failed or it broke.</summary>
    public static CheckovOutcome Parse(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return new CheckovFailed(CheckovFailure.NoJson);
        }

        using (document)
        {
            try
            {
                return Read(document.RootElement);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return new CheckovFailed(CheckovFailure.MalformedResult);
            }
        }
    }

    private static CheckovOutcome Read(JsonElement root)
    {
        // With nothing to report it writes a bare summary with no results, which the canary check then catches.
        var violations = new List<CheckovViolation>();
        var canarySeen = false;

        if (root.TryGetProperty("results", out var results))
        {
            foreach (var failure in results.GetProperty("failed_checks").EnumerateArray())
            {
                var address = failure.GetProperty("resource").GetString() ?? throw new JsonException();
                if (address == ScannablePlan.CanaryAddress)
                {
                    canarySeen = true;
                    continue;
                }

                var checkId = failure.GetProperty("check_id").GetString() ?? throw new JsonException();
                if (Allowlist.TryGetValue(checkId, out var check))
                {
                    violations.Add(new CheckovViolation(checkId, address, check.Severity, check.Title, Attribute(failure)));
                }
            }
        }

        if (!canarySeen)
        {
            return new CheckovFailed(CheckovFailure.CanaryNotSeen);
        }

        return new CheckovRan(
        [
            .. violations
                .Distinct()
                .OrderBy(violation => violation.Address, StringComparer.Ordinal)
                .ThenBy(violation => violation.CheckId, StringComparer.Ordinal),
        ]);
    }

    // Evaluated keys can go deeper, such as security_rule/[0]/access. Pointers stop at the top-level attribute.
    private static string? Attribute(JsonElement failure)
    {
        if (!failure.TryGetProperty("check_result", out var result)
            || !result.TryGetProperty("evaluated_keys", out var keys)
            || keys.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return keys.EnumerateArray()
            .Select(key => key.GetString()?.Split('/', '.', '[')[0].Trim())
            .FirstOrDefault(key => !string.IsNullOrEmpty(key));
    }
}
