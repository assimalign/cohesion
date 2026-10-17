using System.Collections.Generic;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class DiagnosticSessionResult : DatabaseResult
{
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => 0;
    public override IReadOnlyList<Diagnostic> Diagnostics { get; } =
    [
        new Diagnostic { Code = "SQLTEST001", Message = "Test warning", Severity = DiagnosticSeverity.Warning, Start = 0, End = 6, Line = 1 },
        new Diagnostic { Code = "SQL0100", Message = "Statement does not end with ';'.", Severity = DiagnosticSeverity.Information },
    ];
}
