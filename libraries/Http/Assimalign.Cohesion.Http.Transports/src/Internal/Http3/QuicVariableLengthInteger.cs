using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal static class QuicVariableLengthInteger
{
    public static async ValueTask<long?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int bytesRead = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);

        if (bytesRead == 0)
        {
            return null;
        }

        byte first = buffer[0];
        int length = (first >> 6) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 8
        };
        ulong value = (ulong)(first & 0x3F);

        for (int index = 1; index < length; index++)
        {
            bytesRead = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The QUIC variable-length integer was incomplete.");
            }

            value = (value << 8) | buffer[0];
        }

        return (long)value;
    }

    public static long Decode(ReadOnlySpan<byte> buffer, ref int index)
    {
        byte first = buffer[index++];
        int length = (first >> 6) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 8
        };
        ulong value = (ulong)(first & 0x3F);

        for (int offset = 1; offset < length; offset++)
        {
            if (index >= buffer.Length)
            {
                throw new InvalidDataException("The QUIC variable-length integer was incomplete.");
            }

            value = (value << 8) | buffer[index++];
        }

        return (long)value;
    }

    /// <summary>
    /// Attempts to decode a QUIC variable-length integer (RFC 9000 §16) from
    /// the start of a buffered sequence without consuming bytes that are not
    /// yet available. Used for incremental reads off a
    /// <see cref="System.IO.Pipelines.PipeReader"/>.
    /// </summary>
    /// <param name="buffer">The buffered bytes to decode from.</param>
    /// <param name="value">The decoded value when a complete integer is present.</param>
    /// <param name="consumed">The position immediately after the decoded integer.</param>
    /// <returns>
    /// <see langword="true"/> when a complete integer was decoded; otherwise
    /// <see langword="false"/>, indicating more bytes are required.
    /// </returns>
    public static bool TryDecode(ReadOnlySequence<byte> buffer, out long value, out SequencePosition consumed)
    {
        value = 0;
        consumed = buffer.Start;

        SequenceReader<byte> reader = new(buffer);

        if (!reader.TryRead(out byte first))
        {
            return false;
        }

        int length = (first >> 6) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 8
        };
        ulong result = (ulong)(first & 0x3F);

        for (int offset = 1; offset < length; offset++)
        {
            if (!reader.TryRead(out byte next))
            {
                return false;
            }

            result = (result << 8) | next;
        }

        value = (long)result;
        consumed = reader.Position;
        return true;
    }

    public static void Write(Stream stream, long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value < 64)
        {
            stream.WriteByte((byte)value);
            return;
        }

        if (value < 16384)
        {
            ushort encoded = (ushort)(value | 0x4000);
            stream.WriteByte((byte)(encoded >> 8));
            stream.WriteByte((byte)encoded);
            return;
        }

        if (value < 1073741824)
        {
            uint encoded = (uint)(value | 0x80000000);
            stream.WriteByte((byte)(encoded >> 24));
            stream.WriteByte((byte)(encoded >> 16));
            stream.WriteByte((byte)(encoded >> 8));
            stream.WriteByte((byte)encoded);
            return;
        }

        ulong encoded64 = (ulong)value | 0xC000000000000000;
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            stream.WriteByte((byte)(encoded64 >> shift));
        }
    }
}
