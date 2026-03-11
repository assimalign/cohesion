using System;

namespace Assimalign.Cohesion.Database.Storage.Internal;

internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)(crc ^ data[i]);
            crc = Table[index] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        const uint polynomial = 0xEDB88320u;

        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++)
            {
                value = (value & 1u) != 0u ? (value >> 1) ^ polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
