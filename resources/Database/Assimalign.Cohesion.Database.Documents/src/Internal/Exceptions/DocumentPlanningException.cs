using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>A planning or evaluation rejection with a stable diagnostic and the offending node's source span.</summary>
/// <remarks>
/// CD01005 identifies a missing parameter, CD02006 division or remainder by zero
/// (PostgreSQL SQLSTATE 22012), CD02007 a numeric value out of range (22003), and
/// CD02008 an invalid operand or parameter type, and CD01009 colliding parameter keys.
/// CD05001 covers unsupported expressions.
/// </remarks>
internal sealed class DocumentPlanningException : DatabaseException
{
    internal DocumentPlanningException(string code, string message, Location? location = null, Exception? innerException = null)
        : base(message, [CreateDiagnostic(code, message, location)], innerException)
    {
    }

    internal static bool IsExternalFunctionFailure(Exception error, System.Threading.CancellationToken cancellationToken)
        => error is not (DatabaseException or OutOfMemoryException or InsufficientExecutionStackException or TimeoutException or System.IO.IOException or Assimalign.Cohesion.Database.Storage.StorageException)
            && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested);

    internal static DocumentPlanningException FunctionFailed(DocumentFunctionName name, Exception error)
        => new("CD05017", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FunctionFailed, name, error.Message), innerException: error);

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
