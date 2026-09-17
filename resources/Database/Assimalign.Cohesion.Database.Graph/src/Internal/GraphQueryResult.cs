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

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed class GraphQueryResult(IReadOnlyList<QueryColumn> columns, IReadOnlyList<object?[]> rows) : QueryResultSet
{
    public override QueryResultStatus Status => QueryResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
    public override IReadOnlyList<QueryColumn> Columns => columns;
    public override async IAsyncEnumerable<QueryRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GraphQueryRow(row);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class GraphQueryRow(object?[] values) : QueryRow
    {
        public override int FieldCount => values.Length;
        public override bool IsNull(int ordinal) => values[ordinal] is null;
        public override object? GetValue(int ordinal) => values[ordinal];
        public override string? GetString(int ordinal) => values[ordinal] switch
        {
            null => null,
            JsonElement json => json.GetRawText(),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        public override ReadOnlyMemory<byte> GetBytes(int ordinal) => values[ordinal] switch
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
        private object Required(int ordinal) => values[ordinal] ?? throw new DatabaseException($"Field {ordinal} is null.");
    }
}
