using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class GraphExecuteExchange
    : DatabaseProtocolExchange<GraphResultSet>
{
    private readonly GraphProtocolExecuteMessage _request;
    private readonly bool _compositeValues;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphExecuteExchange"/> class.
    /// </summary>
    /// <param name="statement">The graph statement to execute.</param>
    /// <param name="parameters">The named statement parameters, or <see langword="null"/> when the statement has none.</param>
    /// <param name="compositeValues">Whether the session negotiated composite value framing.</param>
    public GraphExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters, bool compositeValues = false)
        : base(GraphProtocol.Family)
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
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the graph exchange before completion.");
            if (hasDiagnostics && frame.Type != ProtocolMessageType.Error && frame.Type != (ProtocolMessageType)GraphProtocolMessageType.ResultComplete)
            {
                throw new ProtocolException(ErrorMessages.DiagnosticsOrderInvalid);
            }
            switch ((byte)frame.Type)
            {
                case (byte)GraphProtocolMessageType.ResultHeader:
                    if (hasHeader) { throw new ProtocolException("A graph result cannot have multiple headers."); }
                    hasHeader = true;
                    var header = GraphProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    var decoded = new GraphColumn[header.Columns.Count];
                    for (int index = 0; index < decoded.Length; index++)
                    {
                        var column = header.Columns[index];
                        decoded[index] = new GraphColumn(column.Name, index, (DatabaseType)column.Type);
                    }
                    columns = decoded;
                    break;
                case (byte)GraphProtocolMessageType.ResultRow:
                    if (!hasHeader) { throw new ProtocolException("A graph row requires a result header."); }
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                case (byte)GraphProtocolMessageType.ResultComplete:
                    if (frame.Payload.Length != sizeof(long)) { throw new ProtocolException("Malformed graph result completion."); }
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
                    throw new ProtocolException($"Unexpected {frame.Type} frame in a graph execute exchange.");
            }
        }
    }

    private object?[] DecodeRow(ReadOnlySpan<byte> payload, int columnCount)
    {
        if (_compositeValues)
        {
            var decoded = new object?[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                if (payload.Length < 4) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
                if (size < 0 || size > payload.Length) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
                decoded[i] = GraphValueCodec.Decode(payload[..size]); payload = payload[size..];
            }
            if (!payload.IsEmpty) { throw new ProtocolException(ErrorMessages.CompositeRowMalformed); }
            return decoded;
        }
        var reader = new DatabaseKeyReader(payload);
        var values = new object?[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            if (reader.IsAtEnd) { throw new ProtocolException("A graph result row has too few values."); }
            values[index] = DatabaseValueCodec.Read(ref reader);
        }
        if (!reader.IsAtEnd) { throw new ProtocolException("A graph result row has too many values."); }
        return values;
    }
}

