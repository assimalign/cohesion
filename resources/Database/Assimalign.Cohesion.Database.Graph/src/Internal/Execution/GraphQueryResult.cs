using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphQueryResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult.DebugView))]
internal sealed class GraphQueryResult : DatabaseResultSet
{
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;
    private readonly IReadOnlyList<Diagnostic> _warnings;
    private readonly long _affected;

    /// <summary>Initializes a new instance of the <see cref="GraphQueryResult"/> class.</summary>
    /// <param name="columns">The projected result columns.</param>
    /// <param name="rows">The materialized result rows, one value array per row.</param>
    /// <param name="warnings">The statement's warnings, such as an unknown label it read (#1228).</param>
    public GraphQueryResult(IReadOnlyList<DatabaseColumn> columns, IReadOnlyList<object?[]> rows, IReadOnlyList<Diagnostic>? warnings = null, IReadOnlyList<bool>? compositeColumns = null, long affected = -1)
    {
        CompositeColumns = compositeColumns;
        _columns = columns;
        _rows = rows;
        _warnings = warnings ?? [];
        _affected = affected;
    }

    internal IReadOnlyList<bool>? CompositeColumns { get; }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _affected;
    public override IReadOnlyList<Diagnostic> Diagnostics => _warnings;
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GraphQueryRow(row);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [global::System.Diagnostics.DebuggerDisplay("GraphQueryRow FieldCount={FieldCount}")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult.GraphQueryRow.DebugView))]
    private sealed class GraphQueryRow : DatabaseRow
    {
        private readonly object?[] _values;

        /// <summary>Initializes a new instance of the <see cref="GraphQueryRow"/> class.</summary>
        /// <param name="values">The row's field values, indexed by ordinal.</param>
        public GraphQueryRow(object?[] values)
        {
            _values = values;
        }

        public override int FieldCount => _values.Length;
        public override bool IsNull(int ordinal) => _values[ordinal] is null;
        public override object? GetValue(int ordinal) => _values[ordinal];
        public override string? GetString(int ordinal) => _values[ordinal] switch
        {
            null => null,
            JsonElement json => json.GetRawText(),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        public override ReadOnlyMemory<byte> GetBytes(int ordinal) => _values[ordinal] switch
        {
            null => ReadOnlyMemory<byte>.Empty,
            JsonElement json => Encoding.UTF8.GetBytes(json.GetRawText()),
            string text => Encoding.UTF8.GetBytes(text),
            _ => throw DatabaseException.CreateGraphFailure("CK02001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphQueryRowGetBytesDatabase, ordinal)),
        };
        public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
        private object Required(int ordinal) => _values[ordinal] ?? throw DatabaseException.CreateGraphFailure("CK02002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphQueryRowRequiredDatabase, ordinal));

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult.GraphQueryRow _target;

            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult.GraphQueryRow value)
            {
                _target = value;
            }

            public int FieldCount => _target.FieldCount;
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryResult value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Execution.DatabaseColumn> Columns => _target.Columns;
    }
}
