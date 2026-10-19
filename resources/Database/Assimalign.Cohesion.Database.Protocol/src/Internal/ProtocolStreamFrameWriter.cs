using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// Writes frames to a stream: header then payload, flushed on demand so callers
/// batch small frames (logical values) into one transport write.
/// </summary>
/// <remarks>
/// This is the writer that touches the transport, so it is the one that writes each frame it wrote
/// to the <c>Assimalign.Cohesion.Database.Protocol</c> event source (<c>FrameWritten</c>, under the
/// <c>Frames</c> keyword): every other writer decorates one of these through its public member, so
/// each wire frame is reported once. The core is already asynchronous, so the trace adds one
/// enabled check per frame and nothing else while no listener takes it.
/// </remarks>
internal sealed class ProtocolStreamFrameWriter : ProtocolFrameWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _header = new byte[ProtocolFrameHeader.Size];

    internal ProtocolStreamFrameWriter(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        // The payload bound is checked by the base's public WriteFrameAsync before this core runs
        // (owner decision 29 of 2026-10-06), so every frame that reaches here is within it.
        frame.Header.WriteTo(_header);
        await _stream.WriteAsync(_header, cancellationToken).ConfigureAwait(false);

        if (!frame.Payload.IsEmpty)
        {
            await _stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }

        ProtocolEventSource.Log.FrameWritten(frame.Type, frame.Payload.Length);
    }

    /// <inheritdoc />
    protected override ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        return new ValueTask(_stream.FlushAsync(cancellationToken));
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        return default;
    }
}
