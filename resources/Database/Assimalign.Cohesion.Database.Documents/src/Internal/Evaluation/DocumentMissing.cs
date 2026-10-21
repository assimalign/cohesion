namespace Assimalign.Cohesion.Database.Documents.Internal;

// Evaluation-only value. JSON projection omits it; it never enters catalog or storage.
internal sealed class DocumentMissing
{
    private DocumentMissing() { }

    internal static DocumentMissing Instance { get; } = new();
}
