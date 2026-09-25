using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Internal;

internal sealed class SqlExecuteExchange : IDatabaseProtocolExchange<DatabaseClientResult>
{
    private readonly ProtocolExecuteMessage _request;

    internal SqlExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
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

    public ProtocolMessageFamily Family => SqlProtocol.Family;

    public bool IsResponseComplete { get; private set; }

    public async ValueTask<DatabaseClientResult> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
    {
        IsResponseComplete = false;
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.Execute, _request.Encode()), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DatabaseClientColumn> columns = [];
        var rows = new List<object?[]>();
        bool hasResultFrames = false;
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange.");
            switch ((byte)frame.Type)
            {
                case (byte)SqlProtocolMessageType.ResultHeader:
                {
                    hasResultFrames = true;
                    ProtocolResultHeaderMessage header = ProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    var decoded = new List<DatabaseClientColumn>(header.Columns.Count);
                    foreach ((string name, byte type) in header.Columns)
                    {
                        decoded.Add(new DatabaseClientColumn(name, (DatabaseType)type));
                    }
                    columns = decoded;
                    break;
                }
                case (byte)SqlProtocolMessageType.ResultRow:
                    hasResultFrames = true;
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                case (byte)SqlProtocolMessageType.ResultComplete:
                {
                    ProtocolResultCompleteMessage complete = ProtocolResultCompleteMessage.Decode(frame.Payload.Span);
                    IsResponseComplete = true;
                    return new DatabaseClientResult(columns, rows, complete.AffectedCount);
                }
                case (byte)ProtocolMessageType.Error:
                {
                    ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    // The SQL server sends these as the entire failed statement
                    // response before any results, then returns to its ready loop.
                    // An Error after result frames cannot certify that boundary.
                    IsResponseComplete = !hasResultFrames &&
                        error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure;
                    throw new DatabaseClientException(error.Code, error.Message);
                }
                default:
                    throw new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Unexpected {frame.Type} frame in a Sql execute exchange.");
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

