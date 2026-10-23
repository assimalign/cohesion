namespace Assimalign.Cohesion.Database.Graph;

// Database owns the exception; Graph owns the catalog-conflict diagnostic.
internal static class DatabaseTransactionAbortedExceptionExtensions
{
    private static readonly string _catalogSnapshotChangedMessage = ErrorMessages.CatalogSnapshotChanged;

    extension(DatabaseTransactionAbortedException)
    {
        internal static void ThrowGraphConflict()
            => throw new DatabaseTransactionAbortedException(_catalogSnapshotChangedMessage,
                [new Types.Diagnostic { Code = "CG04007", Message = _catalogSnapshotChangedMessage, Severity = Types.DiagnosticSeverity.Error }]);
    }
}
