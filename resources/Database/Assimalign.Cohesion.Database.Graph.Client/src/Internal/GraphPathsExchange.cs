using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client;

internal sealed class GraphPathsExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
    : IDatabaseStreamingExchange
{
    private readonly GraphProtocolExecuteMessage _request = GraphRequest.Create(statement, parameters);
    private ProtocolFrame _initial;

    public ProtocolMessageFamily Family => GraphProtocol.Family;

    public async ValueTask OpenAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
        CancellationToken cancellationToken = default)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths, _request.Encode()),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        _initial = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
        ValidateFrame(_initial);
    }

    public async ValueTask CopyToAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer,
        Stream destination, CancellationToken cancellationToken = default)
    {
        ProtocolFrame frame = _initial;
        _initial = default;
        long count = 0;
        byte[] length = new byte[sizeof(int)];
        while (true)
        {
            if (frame.Type == (ProtocolMessageType)GraphProtocolMessageType.PathsComplete)
            {
                if (GraphProtocolPathsCompleteMessage.Decode(frame.Payload.Span).PathCount != count)
                {
                    throw new ProtocolException("The graph path completion count does not match the received paths.");
                }
                return;
            }
            ValidateFrame(frame);
            // This length prefix is an internal bounded handoff, never a new wire format.
            BinaryPrimitives.WriteInt32BigEndian(length, frame.Payload.Length);
            await destination.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
            frame = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateFrame(ProtocolFrame frame)
    {
        if (frame.Type == (ProtocolMessageType)GraphProtocolMessageType.Path)
        {
            _ = GraphProtocolPathMessage.Decode(frame.Payload.Span);
        }
        else if (frame.Type == (ProtocolMessageType)GraphProtocolMessageType.PathsComplete)
        {
            _ = GraphProtocolPathsCompleteMessage.Decode(frame.Payload.Span);
        }
        else
        {
            throw new ProtocolException("Expected a graph path or path completion.");
        }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(IProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("The server closed the connection before completing the graph path exchange.");
        if (frame.Type == ProtocolMessageType.Error)
        {
            ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            throw new DatabaseClientException(error.Code, error.Message);
        }
        return frame;
    }
}
