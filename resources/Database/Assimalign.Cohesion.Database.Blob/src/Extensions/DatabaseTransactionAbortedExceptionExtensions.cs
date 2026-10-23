namespace Assimalign.Cohesion.Database.Blob;

// Database owns the exception; Blob owns the catalog-conflict message.
internal static class DatabaseTransactionAbortedExceptionExtensions
{
    extension(DatabaseTransactionAbortedException)
    {
        internal static void ThrowBlobConflict()
            => throw new DatabaseTransactionAbortedException(ErrorMessages.BlobDatabaseThrowConflictFailure);
    }
}
