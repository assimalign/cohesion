using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A materialized result set: typed columns and fully-evaluated rows. The MVP
/// executor materializes SELECT results (sorting and DISTINCT require it anyway);
/// streaming plans arrive with the cost-based planner.
/// </summary>
internal sealed class SqlMaterializedResultSet : DatabaseResultSet
{
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly List<object?[]> _rows;

    internal SqlMaterializedResultSet(IReadOnlyList<DatabaseColumn> columns, List<object?[]> rows)
    {
        _columns = columns;
        _rows = rows;
        // Transfer each projected row to the result with its own binary cells before ExecuteAsync
        // returns. Literals, function bodies, defaults and parameters may share their source
        // arrays, and GetBytes can expose a backing array just as GetValue does.
        foreach (var row in rows)
        {
            for (int ordinal = 0; ordinal < row.Length; ordinal++)
            {
                if (row[ordinal] is byte[] bytes) { row[ordinal] = bytes.Clone(); }
            }
        }
    }

    /// <inheritdoc />
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;

    /// <inheritdoc />
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
