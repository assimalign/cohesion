using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphExecuteExchange")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Client.Internal.GraphExecuteExchange.DebugView))]
internal sealed class GraphExecuteExchange
    : DatabaseProtocolExchange<GraphResultSet>
{
    private readonly GraphProtocolExecuteMessage _request;
    private readonly bool _compositeValues;
    private bool[] _compositeColumns = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphExecuteExchange"/> class.
    /// </summary>
    /// <param name="statement">The graph statement to execute.</param>
    /// <param name="parameters">The named statement parameters, or <see langword="null"/> when the statement has none.</param>
    /// <param name="compositeValues">Whether the session negotiated composite value framing.</param>
    public GraphExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters, bool compositeValues = false)
        : base(GraphProtocol.Family, transactionStatus: true)
    {
        _compositeValues = compositeValues;
        _request = GraphRequestEncoder.Create(statement, parameters, compositeValues);
    }

    protected override async ValueTask<GraphResultSet> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)GraphProtocolMessageType.Execute, _request.Encode()),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GraphColumn> columns = [];
        var rows = new List<object?[]>();
        IReadOnlyList<ProtocolDiagnostic> diagnostics = [];
        bool hasDiagnostics = false;
        bool hasHeader = false;
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, ErrorMessages.ExecuteCoreAsyncDatabaseClient);
            if (hasDiagnostics && frame.Type != ProtocolMessageType.Error && frame.Type != (ProtocolMessageType)GraphProtocolMessageType.ResultComplete)
            {
                throw new ProtocolException(ErrorMessages.DiagnosticsOrderInvalid);
            }
            switch ((byte)frame.Type)
            {
                case (byte)GraphProtocolMessageType.ResultHeader:
                    if (hasHeader) { throw new ProtocolException(ErrorMessages.ExchangeExecuteCoreAsyncProtocol); }
                    hasHeader = true;
                    var header = GraphProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    _compositeColumns = new bool[header.Columns.Count];
                    var decoded = new GraphColumn[header.Columns.Count];
                    for (int index = 0; index < decoded.Length; index++)
                    {
                        var column = header.Columns[index];
                        if (!_compositeValues && (column.Type & 0x80) != 0) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                        _compositeColumns[index] = _compositeValues && (column.Type & 0x80) != 0;
                        decoded[index] = new GraphColumn(column.Name, index, (DatabaseType)(column.Type & 0x7f));
                    }
                    columns = decoded;
                    break;
                case (byte)GraphProtocolMessageType.ResultRow:
                    if (!hasHeader) { throw new ProtocolException(ErrorMessages.ExchangeExecuteCoreAsyncProtocol2); }
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                case (byte)GraphProtocolMessageType.ResultComplete:
                    if (frame.Payload.Length != sizeof(long)) { throw new ProtocolException(ErrorMessages.ExchangeExecuteCoreAsyncProtocol3); }
                    var complete = GraphProtocolResultCompleteMessage.Decode(frame.Payload.Span);
                    return new GraphResultSet(columns, rows, complete.AffectedCount, diagnostics);
                case (byte)ProtocolMessageType.Diagnostics:
                    diagnostics = ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries;
                    hasDiagnostics = true;
                    break;
                case (byte)ProtocolMessageType.Error:
                    var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    // The ready-loop statement rejection is terminal only before result output.
                    if (!hasHeader &&
                        error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure)
                    {
                        MarkResponseComplete();
                    }
                    throw new DatabaseClientException(error.Code, error.Message, diagnostics, null);
                default:
                    throw new ProtocolException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExchangeExecuteCoreAsyncProtocol4, frame.Type));
            }
        }
    }

    private object?[] DecodeRow(ReadOnlySpan<byte> payload, int columnCount)
    {
        var values = new object?[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            if (_compositeColumns[index])
            {
                if (payload.IsEmpty) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                byte tag = payload[0]; payload = payload[1..];
                if (tag == 1)
                {
                    if (payload.Length < 4) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                    int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
                    if (size < 0 || size > payload.Length) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                    values[index] = GraphValueCodec.Decode(payload[..size]); payload = payload[size..];
                    continue;
                }
                if (tag != 0) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
            }
            int length = ScalarLength(payload);
            values[index] = DatabaseValueCodec.DecodeComponent(payload[..length]); payload = payload[length..];
        }
        if (!payload.IsEmpty) { throw new ProtocolException(ErrorMessages.GraphExecuteExchangeDecodeRowProtocol); }
        return values;
    }
    // Tuple framing is local to the model boundary; validation still belongs to DatabaseValueCodec.
    private static int ScalarLength(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
        var type = (DatabaseType)payload[0];
        int length = type switch
        {
            DatabaseType.Null => 1, DatabaseType.Boolean or DatabaseType.Int8 => 2,
            DatabaseType.Int16 => 3, DatabaseType.Int32 or DatabaseType.Float32 or DatabaseType.Date => 5,
            DatabaseType.Int64 or DatabaseType.Float64 or DatabaseType.Time or DatabaseType.TimeSpan => 9,
            DatabaseType.DateTime => 10, DatabaseType.DateTimeOffset => 11, DatabaseType.Guid => 17,
            _ => 0,
        };
        if (length == 0 && type is DatabaseType.String or DatabaseType.Binary)
        {
            int start = type == DatabaseType.String ? 2 : 1;
            if (type == DatabaseType.String && (payload.Length < 2 || payload[1] != Collation.Binary.Id))
            { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
            for (int i = start; i + 1 < payload.Length; i++)
            {
                if (payload[i] != 0) { continue; }
                if (payload[++i] == 0) { length = i + 1; break; }
                if (payload[i] != 255) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
            }
        }
        if (length == 0 && type == DatabaseType.Decimal && payload.Length >= 2)
        {
            byte sign = payload[1];
            if (sign == 1) { length = 2; }
            else if (sign is 0 or 2)
            {
                byte terminator = sign == 0 ? (byte)255 : (byte)0;
                for (int i = 3; i < payload.Length; i++) { if (payload[i] == terminator) { length = i + 1; break; } }
            }
        }
        if (length == 0 || length > payload.Length) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
        return length;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Client.Internal.GraphExecuteExchange value)
        {
            _ = value;
        }
    }
}
