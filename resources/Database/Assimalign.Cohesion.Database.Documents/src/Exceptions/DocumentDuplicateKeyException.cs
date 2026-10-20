using System.Globalization;

using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>An INSERT found an existing identity, or INSERT/UPSERT repeated a key within one statement.</summary>
public sealed class DocumentDuplicateKeyException : DatabaseException
{
    /// <summary>Initializes a failure for an INSERT whose key already exists in the destination.</summary>
    /// <param name="collectionName">The destination collection.</param>
    /// <param name="documentId">The duplicate document identity.</param>
    public DocumentDuplicateKeyException(string collectionName, DocumentId documentId)
        : this(collectionName, documentId, string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.DuplicateDocumentKey, collectionName, documentId)) { }

    internal static DocumentDuplicateKeyException RepeatedInStatement(string collectionName, DocumentId documentId)
        => new(collectionName, documentId, string.Format(CultureInfo.CurrentCulture, ErrorMessages.RepeatedDocumentKey, documentId));

    private DocumentDuplicateKeyException(string collectionName, DocumentId documentId, string message)
        : base(message, [new Diagnostic { Code = "COHDOC012", Message = message, Severity = DiagnosticSeverity.Error }])
    {
        CollectionName = collectionName;
        DocumentId = documentId;
    }

    /// <summary>Gets the destination collection name.</summary>
    public string CollectionName { get; }
    /// <summary>Gets the duplicate identity.</summary>
    public DocumentId DocumentId { get; }
}
