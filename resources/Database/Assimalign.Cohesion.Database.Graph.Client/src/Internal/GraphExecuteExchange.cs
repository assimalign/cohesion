using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class GraphExecuteExchange
    : IDatabaseProtocolExchange<GraphResultSet>
{
    private readonly GraphProtocolExecuteMessage _request;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphExecuteExchange"/> class.
    /// </summary>
    /// <param name="statement">The graph statement to execute.</param>
    /// <param name="parameters">The named statement parameters, or <see langword="null"/> when the statement has none.</param>
    public GraphExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        _request = GraphRequest.Create(statement, parameters);
    }

    public ProtocolMessageFamily Family => GraphProtocol.Family;
    public bool IsResponseComplete { get; private set; }

    public async ValueTask<GraphResultSet> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
        CancellationToken cancellationToken = default)
    {
        IsResponseComplete = false;
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)GraphProtocolMessageType.Execute, _request.Encode()),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GraphColumn> columns = [];
        var rows = new List<object?[]>();
        bool hasHeader = false;
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the graph exchange before completion.");
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
                    IsResponseComplete = true;
                    return new GraphResultSet(columns, rows, complete.AffectedCount);
                case (byte)ProtocolMessageType.Error:
                    var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    // The ready-loop statement rejection is terminal only before result output.
                    IsResponseComplete = !hasHeader &&
                        error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure;
                    throw new DatabaseClientException(error.Code, error.Message);
                default:
                    throw new ProtocolException($"Unexpected {frame.Type} frame in a graph execute exchange.");
            }
        }
    }

    private static object?[] DecodeRow(ReadOnlySpan<byte> payload, int columnCount)
    {
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

