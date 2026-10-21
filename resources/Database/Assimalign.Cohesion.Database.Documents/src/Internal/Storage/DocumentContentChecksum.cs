using System;

using Assimalign.Cohesion.Database.Documents.Storage;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentContentChecksum
{
    private static readonly uint[] _table = BuildTable();

    internal static uint Append(uint state, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            state = _table[(state ^ value) & 0xff] ^ (state >> 8);
        }

        return state;
    }

    private static uint[] BuildTable()
    {
        var result = new uint[256];
        for (uint index = 0; index < result.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320U);
            }

            result[index] = value;
        }
        return result;
    }
}
