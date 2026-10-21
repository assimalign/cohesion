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
