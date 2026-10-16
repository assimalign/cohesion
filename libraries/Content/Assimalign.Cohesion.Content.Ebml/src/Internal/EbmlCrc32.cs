using System;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

internal static class EbmlCrc32
{
    private static readonly uint[] _table = CreateTable();
    internal static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            state = _table[(state ^ value) & 0xff] ^ (state >> 8);
        }

        return state;
    }
    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
            }

            table[index] = value;
        }
        return table;
    }
}
