using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Internal;

internal sealed class SqlExecuteExchange : DatabaseProtocolExchange<DatabaseClientResult>
{
    private readonly ProtocolExecuteMessage _request;

    internal SqlExecuteExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
        : base(SqlProtocol.Family, transactionStatus: true)
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

    protected override async ValueTask<DatabaseClientResult> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.Execute, _request.Encode()), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DatabaseClientColumn> columns = [];
        var rows = new List<object?[]>();
        IReadOnlyList<ProtocolDiagnostic> diagnostics = [];
        bool hasDiagnostics = false;
        bool hasResultFrames = false;
        while (true)
        {
            ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange.");
            if (hasDiagnostics && frame.Type != ProtocolMessageType.Error && frame.Type != (ProtocolMessageType)SqlProtocolMessageType.ResultComplete)
            {
                throw new ProtocolException(ErrorMessages.DiagnosticsOrderInvalid);
            }
            switch ((byte)frame.Type)
            {
                case (byte)SqlProtocolMessageType.ResultHeader:
                {
                    hasResultFrames = true;
                    ProtocolResultHeaderMessage header = ProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    var decoded = new List<DatabaseClientColumn>(header.Columns.Count);
                    foreach ((string name, byte type, int? maxLength) in header.Columns)
                    {
                        decoded.Add(new DatabaseClientColumn(name, (DatabaseType)type, maxLength));
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
                    return new DatabaseClientResult(columns, rows, complete.AffectedCount, diagnostics);
                }
                case (byte)ProtocolMessageType.Diagnostics:
                    diagnostics = ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries;
                    hasDiagnostics = true;
                    break;
                case (byte)ProtocolMessageType.Error:
                {
                    ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    // ExecutionFailure also terminates a partially delivered result. Throwing
                    // discards the buffered columns/rows and keeps this complete response reusable.
                    if (error.Code == ProtocolErrorCode.ExecutionFailure ||
                        (!hasResultFrames && error.Code == ProtocolErrorCode.ParseFailure))
                    {
                        MarkResponseComplete();
                    }
                    throw new DatabaseClientException(error.Code, error.Message, diagnostics, null);
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

