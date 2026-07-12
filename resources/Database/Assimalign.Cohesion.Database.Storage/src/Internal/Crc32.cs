using System;

namespace Assimalign.Cohesion.Database.Storage.Internal;

internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private const uint initialState = 0xFFFFFFFFu;

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        return Finalize(Append(initialState, data));
    }

    /// <summary>
    /// Begins an incremental CRC computation. Feed segments with <see cref="Append"/>
    /// and complete with <see cref="Finalize"/>.
    /// </summary>
    internal static uint Begin() => initialState;

    internal static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)(state ^ data[i]);
            state = Table[index] ^ (state >> 8);
        }

        return state;
    }

    /// <summary>
    /// Appends <paramref name="count"/> zero bytes to an incremental CRC computation.
    /// </summary>
    internal static uint AppendZeros(uint state, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte index = (byte)state;
            state = Table[index] ^ (state >> 8);
        }

        return state;
    }

    internal static uint Finalize(uint state) => ~state;

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
