using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

// Test-only consumer of the generic client seam; a shipped Graph client remains separate work.
internal static class GraphCatalogTestExtensions
{
    internal static ValueTask<GraphCatalogTestResult> ExecuteAsync(this IDatabaseConnection connection,
        string statement, CancellationToken cancellationToken = default)
        => connection.ExecuteAsync(new GraphCatalogTestExchange(statement), cancellationToken);
}

internal sealed record GraphCatalogTestResult(
    IReadOnlyList<(string Name, DatabaseType Type)> Columns, IReadOnlyList<object?[]> Rows, long AffectedCount);

internal sealed class GraphCatalogTestExchange(string statement) : IDatabaseProtocolExchange<GraphCatalogTestResult>
{
    public ProtocolMessageFamily Family => GraphProtocol.Family;

    public async ValueTask<GraphCatalogTestResult> ExecuteAsync(IProtocolFrameReader reader,
        IProtocolFrameWriter writer, CancellationToken cancellationToken = default)
    {
        await writer.WriteFrameAsync(new((ProtocolMessageType)GraphProtocolMessageType.Execute,
            GraphProtocolExecuteMessage.Create(statement).Encode()), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        var columns = new List<(string Name, DatabaseType Type)>();
        var rows = new List<object?[]>();
        while (true)
        {
            var frame = await reader.ReadFrameAsync(cancellationToken)
                ?? throw new ProtocolException("Catalog connection ended before completion.");
            switch (frame.Type)
            {
                case (ProtocolMessageType)GraphProtocolMessageType.ResultHeader:
                    foreach (var column in GraphProtocolResultHeaderMessage.Decode(frame.Payload.Span).Columns)
                    {
                        columns.Add((column.Name, (DatabaseType)column.Type));
                    }
                    break;
                case (ProtocolMessageType)GraphProtocolMessageType.ResultRow:
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                case (ProtocolMessageType)GraphProtocolMessageType.ResultComplete:
                    return new(columns, rows, GraphProtocolResultCompleteMessage.Decode(frame.Payload.Span).AffectedCount);
                case ProtocolMessageType.Error:
                    var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    throw new DatabaseClientException(error.Code, error.Message);
                default:
                    throw new ProtocolException("Unexpected catalog response.");
            }
        }
    }

    private static object?[] DecodeRow(ReadOnlySpan<byte> payload, int count)
    {
        var reader = new DatabaseKeyReader(payload);
        var values = new object?[count];
        for (int i = 0; i < count; i++) { values[i] = DatabaseValueCodec.Read(ref reader); }
        if (!reader.IsAtEnd) { throw new ProtocolException("Catalog tuple has extra values."); }
        return values;
    }
}
