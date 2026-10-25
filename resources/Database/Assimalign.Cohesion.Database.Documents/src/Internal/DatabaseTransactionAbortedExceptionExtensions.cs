using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Database owns the exception; Documents owns this model-specific conflict message.
internal static partial class DatabaseTransactionAbortedExceptionExtensions
{
    private static readonly string _catalogSnapshotChangedMessage = ErrorMessages.DocumentDatabaseThrowConflictFailure;

    extension(DatabaseTransactionAbortedException)
    {
        internal static void ThrowDocumentConflict()
            => throw new DatabaseTransactionAbortedException(_catalogSnapshotChangedMessage,
                [new Diagnostic { Code = "CD04102", Message = _catalogSnapshotChangedMessage, Severity = DiagnosticSeverity.Error }]);
    }
}
