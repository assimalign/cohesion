using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

internal sealed class KeyValueExecuteExchange : IDatabaseProtocolExchange<KeyValueProtocolResult>
{
    private readonly ProtocolExecuteMessage _request;

    internal KeyValueExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        var encoded = new Dictionary<string, byte[]>(parameters?.Count ?? 0);
        var parameterWriter = new DatabaseKeyWriter();
        if (parameters is not null)
        {
            foreach ((string name, object? value) in parameters)
            {
                parameterWriter.Reset();
                DatabaseValueCodec.Append(parameterWriter, value);
                encoded[name] = parameterWriter.ToArray();
            }
        }
        _request = new ProtocolExecuteMessage(statement, encoded);
    }

    public ProtocolMessageFamily Family => KeyValueProtocol.Family;

    public async ValueTask<KeyValueProtocolResult> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)KeyValueProtocolMessageType.Execute, _request.Encode()), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KeyValueProtocolColumn> columns = [];
        var rows = new List<object?[]>();
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange.");
            switch ((byte)frame.Type)
            {
                case (byte)KeyValueProtocolMessageType.ResultHeader:
                {
                    ProtocolResultHeaderMessage header = ProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    var decoded = new List<KeyValueProtocolColumn>(header.Columns.Count);
                    foreach ((string name, byte type) in header.Columns)
                    {
                        decoded.Add(new KeyValueProtocolColumn(name, (DatabaseType)type));
                    }
                    columns = decoded;
                    break;
                }
                case (byte)KeyValueProtocolMessageType.ResultRow:
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                case (byte)KeyValueProtocolMessageType.ResultComplete:
                {
                    ProtocolResultCompleteMessage complete = ProtocolResultCompleteMessage.Decode(frame.Payload.Span);
                    return new KeyValueProtocolResult(columns, rows, complete.AffectedCount);
                }
                case (byte)ProtocolMessageType.Error:
                {
                    ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    throw new DatabaseClientException(error.Code, error.Message);
                }
                default:
                    throw new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Unexpected {frame.Type} frame in a KeyValue execute exchange.");
            }
        }
    }

    private static object?[] DecodeRow(ReadOnlySpan<byte> payload, int columnCount)
    {
        var values = new List<object?>(columnCount);
        var reader = new DatabaseKeyReader(payload);
        while (!reader.IsAtEnd)
        {
            values.Add(DatabaseValueCodec.Read(ref reader));
        }
        return [.. values];
    }
}

