using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// A materialized key-value result set: fixed typed columns and fully-evaluated
/// rows. Key-value results are naturally small (a point read is one row; scans
/// are bounded by the caller), so materialization keeps the wire exchange and
/// the typed decode simple.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueMaterializedResultSet")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedResultSet.DebugView))]
internal sealed class KeyValueMaterializedResultSet : DatabaseResultSet
{
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly List<object?[]> _rows;
    private readonly long _affectedCount;

    internal KeyValueMaterializedResultSet(IReadOnlyList<DatabaseColumn> columns, List<object?[]> rows, long affectedCount = -1)
    {
        _columns = columns;
        _rows = rows;
        _affectedCount = affectedCount;
    }

    /// <inheritdoc />
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => _affectedCount;

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic> Diagnostics => [];

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;

    /// <inheritdoc />
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new KeyValueMaterializedRow(row);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => default;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedResultSet _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedResultSet value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Execution.DatabaseColumn> Columns => _target.Columns;
    }
}
