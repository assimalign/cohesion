namespace Assimalign.Cohesion.Database.Documents.Internal;

// Evaluation-only value. JSON projection omits it; it never enters catalog or storage.
internal sealed class DocumentMissing
{
    private DocumentMissing() { }

    internal static DocumentMissing Instance { get; } = new();

    // Keep source identity absence in binding frames, without changing ordinary
    // MISSING values or allocating the marker on queries without key joins.
    internal static DocumentMissing UnmatchedSource => SourceMissing.Value;

    private static class SourceMissing
    {
        internal static DocumentMissing Value { get; } = new();
    }
}
