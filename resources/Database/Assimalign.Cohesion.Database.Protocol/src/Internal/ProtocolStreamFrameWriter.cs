using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// Writes frames to a stream: header then payload, flushed on demand so callers
/// batch small frames (logical values) into one transport write.
/// </summary>
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
        // The payload bound belongs to the one writer that encodes the envelope; the
        // decorating writers forward here, so every frame that reaches the wire is checked.
        if ((uint)frame.Payload.Length > ProtocolFrameHeader.MaxPayloadLength)
        {
            throw new ProtocolException(
                $"Frame payload of {frame.Payload.Length} bytes exceeds the {ProtocolFrameHeader.MaxPayloadLength}-byte maximum.");
        }

        frame.Header.WriteTo(_header);
        await _stream.WriteAsync(_header, cancellationToken).ConfigureAwait(false);

        if (!frame.Payload.IsEmpty)
        {
            await _stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
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
