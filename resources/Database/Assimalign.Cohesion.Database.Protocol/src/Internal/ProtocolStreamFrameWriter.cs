using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Writes frames to a stream: header then payload, flushed on demand so callers
/// batch small frames (result rows) into one transport write.
/// </summary>
internal sealed class ProtocolStreamFrameWriter : IProtocolFrameWriter
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
    public async ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
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
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask(_stream.FlushAsync(cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        return default;
    }
}
