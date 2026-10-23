namespace Assimalign.Cohesion.Database.Documents;

// Database owns the exception; Documents owns this model-specific conflict message.
internal static class DatabaseTransactionAbortedExceptionExtensions
{
    extension(DatabaseTransactionAbortedException)
    {
        internal static void ThrowDocumentConflict()
            => throw new DatabaseTransactionAbortedException(ErrorMessages.DocumentDatabaseThrowConflictFailure);
    }
}
