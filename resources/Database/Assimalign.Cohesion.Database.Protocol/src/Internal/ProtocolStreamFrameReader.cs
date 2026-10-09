using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Internal;

/// <summary>
/// Reads frames from a stream: 5-byte header first, then exactly the declared
/// payload. The payload bound is enforced from the header before any payload
/// allocation — an untrusted length prefix can never drive memory use.
/// </summary>
/// <remarks>
/// This is the reader that touches the transport, so it is the one that writes each frame it read
/// to the <c>Assimalign.Cohesion.Database.Protocol</c> event source (<c>FrameRead</c>, under the
/// <c>Frames</c> keyword): every other reader decorates one of these through its public member, so
/// each wire frame is reported once. The core is already asynchronous, so the trace adds one
/// enabled check per frame and nothing else while no listener takes it.
/// </remarks>
internal sealed class ProtocolStreamFrameReader : ProtocolFrameReader
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _header = new byte[ProtocolFrameHeader.Size];

    internal ProtocolStreamFrameReader(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    protected override async ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
    {
        int headerRead = await ReadUpToAsync(_header, ProtocolFrameHeader.Size, cancellationToken).ConfigureAwait(false);

        if (headerRead == 0)
        {
            return null; // clean end of stream between frames
        }

        if (headerRead < ProtocolFrameHeader.Size)
        {
            throw new ProtocolException("The connection ended inside a frame header.");
        }

        if (!ProtocolFrameHeader.TryParse(_header, out var header))
        {
            uint declared = BinaryPrimitives.ReadUInt32BigEndian(_header);
            throw new ProtocolException(
                $"Invalid frame header: declared payload of {declared} bytes exceeds the {ProtocolFrameHeader.MaxPayloadLength}-byte maximum.");
        }

        if (header.PayloadLength == 0)
        {
            ProtocolEventSource.Log.FrameRead(header.Type, 0);
            return new ProtocolFrame(header.Type, ReadOnlyMemory<byte>.Empty);
        }

        var payload = new byte[header.PayloadLength];
        int payloadRead = await ReadUpToAsync(payload, payload.Length, cancellationToken).ConfigureAwait(false);

        if (payloadRead < payload.Length)
        {
            throw new ProtocolException("The connection ended inside a frame payload.");
        }

        ProtocolEventSource.Log.FrameRead(header.Type, payload.Length);
        return new ProtocolFrame(header.Type, payload);
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

    private async ValueTask<int> ReadUpToAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        int total = 0;

        while (total < count)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
