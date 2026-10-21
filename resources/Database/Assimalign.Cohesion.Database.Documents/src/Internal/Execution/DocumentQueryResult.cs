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

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentQueryResult : DatabaseResultSet
{
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;
    private readonly IReadOnlyList<ReadOnlyMemory<byte>>? _rawDocuments;

    internal bool BareValue { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentQueryResult"/> class.
    /// </summary>
    /// <param name="columns">The column descriptors of the result set.</param>
    /// <param name="rows">The materialized row values, one array per row in column order.</param>
    /// <param name="bareValue">Whether frames contain a bare value instead of a projection object.</param>
    /// <param name="rawDocuments">The original stored bytes for lone-star results.</param>
    public DocumentQueryResult(IReadOnlyList<DatabaseColumn> columns, IReadOnlyList<object?[]> rows, bool bareValue = false, IReadOnlyList<ReadOnlyMemory<byte>>? rawDocuments = null)
    {
        _columns = columns;
        _rows = rows;
        BareValue = bareValue;
        _rawDocuments = rawDocuments;
    }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < _rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DocumentQueryRow(_rows[index], _rawDocuments?[index] ?? default, BareValue);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal sealed class DocumentQueryRow : DatabaseRow
    {
        private readonly object?[] _values;
        private readonly bool _bareValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentQueryRow"/> class.
        /// </summary>
        /// <param name="values">The field values of the row in column order.</param>
        /// <param name="rawContent">The original stored bytes for a lone-star result.</param>
        /// <param name="bareValue">Whether the row contains one bare JSON value.</param>
        public DocumentQueryRow(object?[] values, ReadOnlyMemory<byte> rawContent, bool bareValue = false) : base(supportsMissing: true)
        {
            _values = values;
            RawContent = rawContent;
            _bareValue = bareValue;
        }

        internal ReadOnlyMemory<byte> RawContent { get; }

        public override int FieldCount => _values.Length;
        public override bool IsNull(int ordinal) => _values[ordinal] is null;
        protected override bool IsMissingCore(int ordinal) => _values[ordinal] is DocumentMissing;
        public override object? GetValue(int ordinal) => _values[ordinal] is DocumentMissing ? null : _values[ordinal];
        public override string? GetString(int ordinal) => GetValue(ordinal) switch
        {
            null => null,
            JsonElement json => json.GetRawText(),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        public override ReadOnlyMemory<byte> GetBytes(int ordinal)
            => ordinal == 0 && !RawContent.IsEmpty ? RawContent : _bareValue ? DocumentJsonValue.Encode(GetValue(ordinal)) : GetValue(ordinal) switch
            {
                null => ReadOnlyMemory<byte>.Empty,
                JsonElement json => Encoding.UTF8.GetBytes(json.GetRawText()),
                string text => Encoding.UTF8.GetBytes(text),
                _ => throw new DatabaseException(string.Format(CultureInfo.InvariantCulture, ErrorMessages.RowBytesInvalid, ordinal)),
            };
        public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
        private object Required(int ordinal) => GetValue(ordinal) ?? throw new DatabaseException($"Field {ordinal} is null.");
    }
}
