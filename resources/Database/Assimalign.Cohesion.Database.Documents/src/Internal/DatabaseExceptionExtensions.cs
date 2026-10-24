using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// The root owns the exception; this model owns its failure conditions and resource messages.
internal static partial class DatabaseExceptionExtensions
{
    extension(DatabaseException)
    {
        internal static DatabaseException CreateDocumentsFailure(string code, string message)
            => new(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }]);
    }
}
