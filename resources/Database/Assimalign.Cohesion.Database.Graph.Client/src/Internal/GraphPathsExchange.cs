using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class GraphPathsExchange
    : DatabaseStreamingExchange
{
    private readonly GraphProtocolExecuteMessage _request;
    private ProtocolFrame _initial;
    private IReadOnlyList<ProtocolDiagnostic> _diagnostics = [];
    private bool _hasDiagnostics;

    internal IReadOnlyList<ProtocolDiagnostic> Diagnostics => _diagnostics;

    internal DatabaseClientException? Rejection { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphPathsExchange"/> class.
    /// </summary>
    /// <param name="statement">The graph statement whose matched paths are streamed.</param>
    /// <param name="parameters">The named statement parameters, or <see langword="null"/> when the statement has none.</param>
    public GraphPathsExchange(string statement, IReadOnlyDictionary<string, object?>? parameters)
        : base(GraphProtocol.Family)
    {
        _request = GraphRequestEncoder.Create(statement, parameters);
    }

    protected override async ValueTask OpenCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths, _request.Encode()),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        _initial = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
        if (_initial.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(_initial.Payload.Span);
            if (error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure)
            {
                // A terminal rejection before path output is a complete, healthy exchange.
                // Finish the shared stream before GraphConnection raises the retained failure.
                Rejection = new DatabaseClientException(error.Code, error.Message, _diagnostics, null);
                return;
            }
        }
        ValidateFrame(_initial);
    }

    protected override async ValueTask CopyToCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        Stream destination, CancellationToken cancellationToken)
    {
        ProtocolFrame frame = _initial;
        _initial = default;
        if (Rejection is not null) { return; }
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

    private void ValidateFrame(ProtocolFrame frame)
    {
        if (frame.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            throw new DatabaseClientException(error.Code, error.Message, _diagnostics, null);
        }
        else if (frame.Type == (ProtocolMessageType)GraphProtocolMessageType.Path)
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

    private async ValueTask<ProtocolFrame> ReadAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        ProtocolFrame frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("The server closed the connection before completing the graph path exchange.");
        if (frame.Type == ProtocolMessageType.Diagnostics)
        {
            if (_hasDiagnostics) { throw new ProtocolException(ErrorMessages.GraphDiagnosticsDuplicate); }
            _hasDiagnostics = true;
            _diagnostics = ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries;
            frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException(ErrorMessages.GraphDiagnosticsCompletionMissing);
            if (frame.Type != ProtocolMessageType.Error && frame.Type != (ProtocolMessageType)GraphProtocolMessageType.PathsComplete)
            {
                throw new ProtocolException(ErrorMessages.GraphDiagnosticsOrderInvalid);
            }
        }

        return frame;
    }
}
