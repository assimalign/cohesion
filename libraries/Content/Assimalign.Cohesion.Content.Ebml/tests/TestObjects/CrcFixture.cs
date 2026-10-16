using System;
using System.Buffers.Binary;
using System.IO;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// Synthesized RFC fixtures. The bitwise reference is independent of the production lookup table;
// "123456789" is the standard IEEE CRC-32 check vector.
internal static class CrcFixture
{
    internal static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var item in bytes)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }
        return ~crc;
    }

    internal static byte[] Master(ulong id, byte[] children, bool unknown = false)
    {
        using var output = new MemoryStream();
        VInt.FromEncoded(id).Write(output);
        (unknown ? VInt.UnknownSize(1) : VInt.EncodeSize((ulong)children.Length + 6)).Write(output);
        output.Write(new byte[] { 0xBF, 0x84 });
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Compute(children));
        output.Write(crc);
        output.Write(children);
        return output.ToArray();
    }
}
