using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>A planner rejection with a stable diagnostic and the offending node's source span.</summary>
internal sealed class DocumentPlanningException : DatabaseException
{
    internal DocumentPlanningException(string code, string message, Location? location = null, Exception? innerException = null)
        : base(message, [new Diagnostic
        {
            Code = code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
            Location = location is null ? null : DiagnosticLocation.Absolute,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
        }], innerException)
    {
    }
}
