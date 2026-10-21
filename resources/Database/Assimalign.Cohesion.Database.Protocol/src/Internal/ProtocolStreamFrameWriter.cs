using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// Writes each complete frame in one stream write. The underlying stream can flush
/// on write; callers can also explicitly flush through this writer.
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
        if (frame.Payload.IsEmpty)
        {
            frame.Header.WriteTo(_header);
            await _stream.WriteAsync(_header, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int length = ProtocolFrameHeader.Size + frame.Payload.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                frame.Header.WriteTo(buffer);
                frame.Payload.CopyTo(buffer.AsMemory(ProtocolFrameHeader.Size));
                await _stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
