using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A materialized result set: typed columns and fully-evaluated rows. The MVP
/// executor materializes SELECT results (sorting and DISTINCT require it anyway);
/// streaming plans arrive with the cost-based planner.
/// </summary>
internal sealed class SqlMaterializedResultSet : QueryResultSet
{
    private readonly IReadOnlyList<QueryColumn> _columns;
    private readonly List<object?[]> _rows;

    internal SqlMaterializedResultSet(IReadOnlyList<QueryColumn> columns, List<object?[]> rows)
    {
        _columns = columns;
        _rows = rows;
    }

    /// <inheritdoc />
    public override QueryResultStatus Status => QueryResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;

    /// <inheritdoc />
    public override IReadOnlyList<QueryColumn> Columns => _columns;

    /// <inheritdoc />
    public override async IAsyncEnumerable<QueryRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SqlMaterializedRow(row);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => default;
}
