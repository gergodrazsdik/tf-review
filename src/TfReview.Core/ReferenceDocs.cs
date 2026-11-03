namespace TfReview.Core;

/// <summary>What the rest of the code needs to know about imported documents (D42).</summary>
public static class ReferenceDocs
{
    /// <summary>Starts every reference chunk's ID, as docs/contracts/reference-chunk.schema.json requires.</summary>
    public const string IdPrefix = "REF-";

    public static bool IsReference(string documentId) => documentId.StartsWith(IdPrefix, StringComparison.Ordinal);
}
