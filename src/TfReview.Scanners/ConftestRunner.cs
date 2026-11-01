using System.Text.Json;
using System.Text.RegularExpressions;
using TfReview.Core;

namespace TfReview.Scanners;

/// <summary>Why conftest's result can't be trusted. Each one means the plan wasn't checked.</summary>
public enum ConftestFailure
{
    CouldNotRun,
    NoJson,
    NoPolicyRan,
    MalformedResult,
}

public closed record ConftestOutcome;

/// <summary>
/// One violation per policy failure, sorted so the bytes are stable. <see cref="ConftestViolation.Message"/>
/// includes resource names from the PR, so it's untrusted text.
/// </summary>
public sealed record ConftestRan(IReadOnlyList<ConftestViolation> Violations) : ConftestOutcome
{
    /// <summary>
    /// One finding per resource and rule, because ADR-002 fails once per missing tag. The policies don't
    /// say which attribute failed, so the evidence points at the resource's planned values.
    /// </summary>
    public IReadOnlyList<Finding> ToFindings() =>
    [
        .. Violations
            .GroupBy(violation => (violation.Address, violation.DocId))
            .Select(group => new Finding(
                group.Key.Address,
                group.Key.DocId,
                group.Max(violation => violation.Severity),
                Reporter.Scanner,
                Deterministic: true,
                string.Join("; ", group.Select(violation => violation.Message)),
                [
                    new PlanEvidence(CondensedPlan.PointerTo(group.Key.Address, "change", "after")),
                    new DocEvidence(group.Key.DocId),
                ]))
            .OrderBy(finding => finding.ResourceAddress, StringComparer.Ordinal)
            .ThenBy(finding => finding.Rule, StringComparer.Ordinal),
    ];
}

public sealed record ConftestViolation(string DocId, string Address, Severity Severity, string Message);

public sealed record ConftestFailed(ConftestFailure Reason) : ConftestOutcome;

/// <summary>
/// Runs the standards' Rego over a redacted plan. It takes the redacted plan rather than the condensed
/// one, because condensing removes attributes the policies read: POL-001 would report them as missing.
/// </summary>
public sealed partial class ConftestRunner(IProcessRunner processes, string program = "conftest")
{
    private const string DefaultNamespace = "main";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<ConftestOutcome> RunAsync(
        RedactedPlan plan, string standardsDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Directory.CreateTempSubdirectory("tfreview-");
        try
        {
            var path = Path.Combine(directory.FullName, "plan.json");
            await File.WriteAllTextAsync(path, plan.ToCanonicalJson(), cancellationToken);

            // Our policies have their own packages. Without --all-namespaces conftest only reads "main",
            // finds nothing there, and reports a clean result that checked nothing.
            var result = await processes.RunAsync(
                program,
                ["test", "--no-color", "--all-namespaces", "--output", "json", "--parser", "json", "--policy", standardsDirectory, path],
                Timeout,
                cancellationToken);

            return result is null ? new ConftestFailed(ConftestFailure.CouldNotRun) : Parse(result.Output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Reads conftest's output. The exit code can't be used: it's 1 both when policies fail and when
    /// conftest itself errors, for example on a broken policy. Only valid JSON means it really ran.
    /// </summary>
    public static ConftestOutcome Parse(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return new ConftestFailed(ConftestFailure.NoJson);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ConftestFailed(ConftestFailure.NoJson);
            }

            try
            {
                return Read(document.RootElement);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException)
            {
                return new ConftestFailed(ConftestFailure.MalformedResult);
            }
        }
    }

    private static ConftestOutcome Read(JsonElement results)
    {
        var violations = new List<ConftestViolation>();
        var policyRan = false;

        foreach (var result in results.EnumerateArray())
        {
            policyRan |= result.GetProperty("namespace").GetString() != DefaultNamespace;
            if (!result.TryGetProperty("failures", out var failures))
            {
                continue;
            }

            foreach (var failure in failures.EnumerateArray())
            {
                violations.Add(ReadViolation(failure));
            }
        }

        if (!policyRan)
        {
            return new ConftestFailed(ConftestFailure.NoPolicyRan);
        }

        return new ConftestRan(
        [
            .. violations
                .Distinct()
                .OrderBy(violation => violation.Address, StringComparer.Ordinal)
                .ThenBy(violation => violation.DocId, StringComparer.Ordinal)
                .ThenBy(violation => violation.Message, StringComparer.Ordinal),
        ]);
    }

    private static ConftestViolation ReadViolation(JsonElement failure)
    {
        var details = failure.GetProperty("metadata").GetProperty("details");
        var docId = details.GetProperty("doc_id").GetString() ?? string.Empty;
        var address = details.GetProperty("address").GetString() ?? string.Empty;
        var severity = details.GetProperty("severity").GetString() switch
        {
            "low" => Severity.Low,
            "medium" => Severity.Medium,
            "high" => Severity.High,
            _ => throw new JsonException("Unknown severity."),
        };

        if (!DocId().IsMatch(docId) || address.Length == 0)
        {
            throw new JsonException("Malformed policy details.");
        }

        return new ConftestViolation(docId, address, severity, failure.GetProperty("msg").GetString() ?? string.Empty);
    }

    [GeneratedRegex("^(ADR|POL|DES)-[0-9]{3}$")]
    private static partial Regex DocId();
}
