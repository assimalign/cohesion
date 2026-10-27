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

[global::System.Diagnostics.DebuggerDisplay("DocumentQueryResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult.DebugView))]
internal sealed class DocumentQueryResult : DatabaseResultSet
{
    private readonly long _affectedCount;
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;
    private readonly IReadOnlyList<ReadOnlyMemory<byte>>? _rawDocuments;
    private readonly DocumentAggregateMemory? _memory;
    private readonly int _memoryGeneration;
    private long _retainedBytes;

    internal bool BareValue { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentQueryResult"/> class.
    /// </summary>
    /// <param name="columns">The column descriptors of the result set.</param>
    /// <param name="rows">The materialized row values, one array per row in column order.</param>
    /// <param name="bareValue">Whether frames contain a bare value instead of a projection object.</param>
    /// <param name="rawDocuments">The original stored bytes for lone-star results.</param>
    /// <param name="affectedCount">The mutation count, or -1 for a read-only result.</param>
    /// <param name="memory">The statement counter while this result is an executing SELECT source.</param>
    /// <param name="retainedBytes">The result reservations transferred by the executor.</param>
    public DocumentQueryResult(IReadOnlyList<DatabaseColumn> columns, IReadOnlyList<object?[]> rows, bool bareValue = false, IReadOnlyList<ReadOnlyMemory<byte>>? rawDocuments = null, long affectedCount = -1,
        DocumentAggregateMemory? memory = null, long retainedBytes = 0)
    {
        _affectedCount = affectedCount;
        _columns = columns;
        _rows = rows;
        BareValue = bareValue;
        _rawDocuments = rawDocuments;
        _memory = memory;
        _memoryGeneration = memory?.Generation ?? 0;
        _retainedBytes = retainedBytes;
    }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _affectedCount;
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
    public override ValueTask DisposeAsync()
    {
        _memory?.ReleaseResult(Interlocked.Exchange(ref _retainedBytes, 0), _memoryGeneration);
        return ValueTask.CompletedTask;
    }

    [global::System.Diagnostics.DebuggerDisplay("DocumentQueryRow FieldCount={FieldCount}")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult.DocumentQueryRow.DebugView))]
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
                _ => throw DatabaseException.CreateDocumentsFailure("CK02001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.RowBytesInvalid, ordinal)),
            };
        public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
        public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
        public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
        private object Required(int ordinal) => GetValue(ordinal) ?? throw DatabaseException.CreateDocumentsFailure("CK02002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DocumentQueryRowRequiredDatabase, ordinal));

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult.DocumentQueryRow _target;

            public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult.DocumentQueryRow value)
            {
                _target = value;
            }

            public int FieldCount => _target.FieldCount;
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentQueryResult value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Execution.DatabaseColumn> Columns => _target.Columns;
    }
}
