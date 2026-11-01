using TfReview.Core;
using TfReview.Rag;

namespace TfReview.Agents;

/// <summary>The findings that have evidence, and how many were dropped for each reason.</summary>
public sealed record EvidenceCheck(IReadOnlyList<Finding> Kept, IReadOnlyDictionary<string, int> Drops);

/// <summary>
/// Drops findings that can't be tied to this plan and these standards, which is what stands between a
/// model's invention and the PR. Scanner findings need a plan pointer. Model findings also need a document
/// that was in their prompt and applies to that resource type, and have to be about something this plan
/// changes, while a policy can still report an old problem. Drops are counted and never retried.
/// </summary>
public static class EvidenceChecker
{
    public const string NoPlanPointer = "no_plan_pointer";
    public const string PointerUnresolved = "pointer_unresolved";
    public const string PointerOtherResource = "pointer_other_resource";
    public const string AttributeUnchanged = "attribute_unchanged";
    public const string NoDocument = "no_document";
    public const string DocumentNotRetrieved = "document_not_retrieved";
    public const string DocumentNotApplicable = "document_not_applicable";

    /// <param name="findings">Findings to check, from scanners and agents.</param>
    /// <param name="plan">The plan the pointers must resolve in.</param>
    /// <param name="retrieved">The documents that were in the prompt. The retriever only returns accepted ones.</param>
    public static EvidenceCheck Check(IEnumerable<Finding> findings, CondensedPlan plan, IReadOnlyCollection<StandardsDoc> retrieved)
    {
        var kept = new List<Finding>();
        var drops = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            if (DropReason(finding, plan, retrieved) is { } reason)
            {
                drops[reason] = drops.GetValueOrDefault(reason) + 1;
            }
            else
            {
                kept.Add(finding);
            }
        }

        return new EvidenceCheck(kept, drops);
    }

    private static string? DropReason(Finding finding, CondensedPlan plan, IReadOnlyCollection<StandardsDoc> retrieved)
    {
        var pointers = finding.Evidence.Select(item => item.Value).OfType<PlanEvidence>().ToList();
        if (pointers.Count == 0)
        {
            return NoPlanPointer;
        }

        foreach (var pointer in pointers)
        {
            if (!plan.Resolves(pointer.PlanPointer))
            {
                return PointerUnresolved;
            }

            if (CondensedPlan.AddressOf(pointer.PlanPointer) != finding.ResourceAddress)
            {
                return PointerOtherResource;
            }
        }

        if (finding.Deterministic)
        {
            return null;
        }

        return pointers.Any(pointer => plan.PointsAtUnchangedAttribute(pointer.PlanPointer))
            ? AttributeUnchanged
            : DocumentDropReason(finding, plan, retrieved);
    }

    private static string? DocumentDropReason(Finding finding, CondensedPlan plan, IReadOnlyCollection<StandardsDoc> retrieved)
    {
        var documents = finding.Evidence.Select(item => item.Value).OfType<DocEvidence>().ToList();
        if (documents.Count == 0)
        {
            return NoDocument;
        }

        var resourceType = plan.TypeOf(finding.ResourceAddress);
        foreach (var document in documents)
        {
            var doc = retrieved.FirstOrDefault(candidate => candidate.Id == document.DocId);
            if (doc is null)
            {
                return DocumentNotRetrieved;
            }

            if (resourceType is null || !doc.AppliesToType(resourceType))
            {
                return DocumentNotApplicable;
            }
        }

        return null;
    }
}
