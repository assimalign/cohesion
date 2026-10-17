using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>A planning or evaluation rejection with a stable diagnostic and the offending node's source span.</summary>
/// <remarks>
/// COHDOC005 identifies a missing parameter, COHDOC006 division or remainder by zero
/// (PostgreSQL SQLSTATE 22012), COHDOC007 a numeric value out of range (22003), and
/// COHDOC008 an invalid operand or parameter type. COHDOC001 covers unsupported expressions.
/// </remarks>
internal sealed class DocumentPlanningException : DatabaseException
{
    internal DocumentPlanningException(string code, string message, Location? location = null, Exception? innerException = null)
        : base(message, [CreateDiagnostic(code, message, location)], innerException)
    {
    }

    internal static Diagnostic CreateDiagnostic(string code, string? message, Location? location) => new()
    {
        Code = code,
        Message = message,
        Severity = DiagnosticSeverity.Error,
        Location = location is null ? null : DiagnosticLocation.Absolute,
        Start = location?.Start,
        End = location?.End,
        Line = location?.StartLine ?? 1,
    };

    internal DocumentPlanningException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Diagnostics[0].Code!, Message, location, InnerException);
}
