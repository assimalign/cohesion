using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentQueryResult : QueryResultSet
{
    private readonly IReadOnlyList<QueryColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentQueryResult"/> class.
    /// </summary>
    /// <param name="columns">The column descriptors of the result set.</param>
    /// <param name="rows">The materialized row values, one array per row in column order.</param>
    public DocumentQueryResult(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows)
    {
        _columns = columns;
        _rows = rows;
    }

    public override QueryResultStatus Status => QueryResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
    public override IReadOnlyList<QueryColumn> Columns => _columns;
    public override async IAsyncEnumerable<QueryRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DocumentQueryRow(row);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class DocumentQueryRow : QueryRow
    {
        private readonly object?[] _values;

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentQueryRow"/> class.
        /// </summary>
        /// <param name="values">The field values of the row in column order.</param>
        public DocumentQueryRow(object?[] values)
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
            _ => throw new DatabaseException($"Field {ordinal} is not JSON or a string."),
        };
        public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
        private object Required(int ordinal) => _values[ordinal] ?? throw new DatabaseException($"Field {ordinal} is null.");
    }
}
