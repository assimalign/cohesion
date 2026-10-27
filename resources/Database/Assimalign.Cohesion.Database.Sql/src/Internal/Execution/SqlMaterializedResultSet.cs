using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A materialized result set: typed columns and fully-evaluated output rows.
/// SELECT filters, projects and selects bounded windows during execution, but
/// completes before delivery so statement resources never escape into enumeration.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlMaterializedResultSet")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlMaterializedResultSet.DebugView))]
internal sealed class SqlMaterializedResultSet : DatabaseResultSet
{
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly List<object?[]> _rows;

    internal SqlMaterializedResultSet(IReadOnlyList<DatabaseColumn> columns, List<object?[]> rows, long affectedCount = -1)
    {
        _columns = columns;
        _rows = rows;
        AffectedCount = affectedCount;
    }

    /// <inheritdoc />
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount { get; }

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic> Diagnostics => [];

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;

    // Admit query output retained by CTAS alongside its encoded batch and any other
    // statement reservations. The caller releases this grant after source disposal.
    internal long ReserveMemory(SqlStatementMetrics metrics, long budget, CancellationToken cancellationToken)
    {
        long reserved = 0;
        try
        {
            Reserve(24L + 8L * _rows.Capacity);
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reserve(SqlHashJoinTable.RowBytes(row));
            }
            return reserved;
        }
        catch
        {
            metrics.ReleaseHashMemory(reserved);
            throw;
        }

        void Reserve(long bytes)
        {
            metrics.ReserveHashMemory(bytes, budget);
            reserved += bytes;
        }
    }

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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlMaterializedResultSet value)
        {
            _ = value;
        }
    }
}
