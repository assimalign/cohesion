using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;

internal sealed class KeyValueExecuteExchange : DatabaseProtocolExchange<KeyValueProtocolResult>
{
    private readonly ProtocolExecuteMessage _request;

    internal KeyValueExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
        : base(KeyValueProtocol.Family)
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

    protected override async ValueTask<KeyValueProtocolResult> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)KeyValueProtocolMessageType.Execute, _request.Encode()), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KeyValueProtocolColumn> columns = [];
        var rows = new List<object?[]>();
        bool hasResultFrames = false;
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange.");
            switch ((byte)frame.Type)
            {
                case (byte)KeyValueProtocolMessageType.ResultHeader:
                {
                    hasResultFrames = true;
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
                    hasResultFrames = true;
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
                    // The key-value server sends these as the entire failed statement
                    // response before any results, then returns to its ready loop.
                    // An Error after result frames cannot certify that boundary.
                    if (!hasResultFrames &&
                        error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure)
                    {
                        MarkResponseComplete();
                    }
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

