using System;
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
        int length = first >> 6 switch
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
        int length = first >> 6 switch
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
