using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Blob.Internal;

// Database owns the exception; Blob owns the catalog-conflict message.
internal static partial class DatabaseTransactionAbortedExceptionExtensions
{
    private static readonly string _catalogSnapshotChangedMessage = ErrorMessages.BlobDatabaseThrowConflictFailure;

    extension(DatabaseTransactionAbortedException)
    {
        internal static void ThrowBlobConflict()
            => throw new DatabaseTransactionAbortedException(_catalogSnapshotChangedMessage,
                [new Diagnostic { Code = "CB04002", Message = _catalogSnapshotChangedMessage, Severity = DiagnosticSeverity.Error }]);
    }
}
