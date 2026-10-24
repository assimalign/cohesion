using System;

using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule per shared-source design: these assembly-local
// extensions retain the Database namespace when compiled into model assemblies.
namespace Assimalign.Cohesion.Database;

internal static partial class DatabaseExceptionExtensions
{
    extension(DatabaseException)
    {
        internal static DatabaseException CreateEngineFailure(string code, string message, Exception? innerException = null)
            => new(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }], innerException);
    }

    extension(DatabaseNotFoundException)
    {
        internal static DatabaseNotFoundException CreateEngineAbsence(string code, string message, Exception? innerException = null)
            => new(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }], innerException);
    }
}
