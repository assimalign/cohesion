using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class DiagnosticOverflowResult : DatabaseResultSet
{
    private readonly DatabaseResultSet _result;

    internal DiagnosticOverflowResult(DatabaseResultSet result, IReadOnlyList<Diagnostic> diagnostics)
    {
        _result = result;
        Diagnostics = diagnostics;
    }

    public override DatabaseResultStatus Status => _result.Status;
    public override long AffectedCount => _result.AffectedCount;
    public override IReadOnlyList<Diagnostic> Diagnostics { get; }
    public override IReadOnlyList<DatabaseColumn> Columns => _result.Columns;
    public override IAsyncEnumerable<DatabaseRow> GetRowsAsync(CancellationToken cancellationToken = default)
        => _result.GetRowsAsync(cancellationToken);
    public override ValueTask DisposeAsync() => _result.DisposeAsync();
}
