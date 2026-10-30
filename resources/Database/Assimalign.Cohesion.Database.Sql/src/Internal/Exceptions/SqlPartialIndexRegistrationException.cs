using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A reachable predicate conversion no longer has a proven immutable registration.</summary>
internal sealed class SqlPartialIndexRegistrationException : DatabaseException
{
    internal SqlPartialIndexRegistrationException(string signature, string reason, Location? location)
        : base(reason, [new Diagnostic
        {
            Code = "CS05091",
            Message = reason,
            Severity = DiagnosticSeverity.Error,
            Location = location is null ? null : DiagnosticLocation.Absolute,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
        }])
    {
        Signature = signature;
    }

    internal string Signature { get; }
}
