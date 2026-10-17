using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A planner rejection with a stable diagnostic and the offending node's source span.</summary>
internal sealed class SqlPlanningException : DatabaseException
{
    internal SqlPlanningException(string code, string message, Location? location = null, Exception? innerException = null)
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

    internal static SqlPlanningException IdentityViolation(string column, string reason)
        => new("COHSQLE025", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityColumnViolation, column, reason));

    internal SqlPlanningException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Diagnostics[0].Code!, Message, location, InnerException);
}
