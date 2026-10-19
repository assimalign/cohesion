using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Marks an unsupported query shape discovered during catalog binding, so the
/// execution boundary returns the same language diagnostic as parser exclusions.
/// </summary>
internal sealed class SqlUnsupportedQueryException : DatabaseException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlUnsupportedQueryException"/> class.
    /// </summary>
    /// <param name="message">The unsupported form and its supported boundary.</param>
    /// <param name="innerException">The binding failure that identified the form.</param>
    public SqlUnsupportedQueryException(string message, Exception? innerException = null, Location? location = null)
        : base(message, [new Diagnostic
        {
            Code = "COHDBL001", Message = message, Severity = DiagnosticSeverity.Error,
            Start = location?.Start, End = location?.End, Line = location?.StartLine ?? 1,
            Location = location is null ? null : DiagnosticLocation.Absolute,
        }], innerException)
    {
    }
}
