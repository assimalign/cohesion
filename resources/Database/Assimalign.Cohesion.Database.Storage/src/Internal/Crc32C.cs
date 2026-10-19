using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// CRC-32C (Castagnoli, reflected polynomial <c>0x82F63B78</c>, initial value and final
/// XOR <c>0xFFFFFFFF</c>): the checksum of every journal frame, every page and both
/// header slots of page 0 (storage format 2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BitOperations.Crc32C(uint, ulong)"/> compiles to the CRC32C instruction on
/// x64 (SSE4.2) and ARM64 (the CRC32 extension) and falls back to a software table
/// elsewhere, so one code path serves every platform NativeAOT targets. It consumes eight
/// bytes per step; over an 8 KiB page that is about 1.3 µs against 27–30 µs for the
/// byte-at-a-time IEEE table it replaced (#1251). PostgreSQL made the same choice for its
/// WAL and control file (<c>src/include/port/pg_crc32c.h</c>, <c>xl_crc</c> in
/// <c>src/include/access/xlogrecord.h</c>).
/// </para>
/// <para>
/// The instruction folds a value's bytes in little-endian order, so each word is read
/// little-endian: the checksum of a byte sequence is the same on every host.
/// </para>
/// </remarks>
internal static class Crc32C
{
    private const uint InitialState = 0xFFFFFFFFu;

    /// <summary>
    /// Computes the CRC-32C of <paramref name="data"/>.
    /// </summary>
    internal static uint Compute(ReadOnlySpan<byte> data) => Finalize(Append(InitialState, data));

    /// <summary>
    /// Begins an incremental CRC computation. Feed segments with <see cref="Append"/> or
    /// <see cref="AppendZeros"/> and complete with <see cref="Finalize"/>.
    /// </summary>
    internal static uint Begin() => InitialState;

    /// <summary>
    /// Folds <paramref name="data"/> into an incremental CRC computation.
    /// </summary>
    internal static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        ref byte start = ref MemoryMarshal.GetReference(data);
        int length = data.Length;
        int offset = 0;

        for (; length - offset >= sizeof(ulong); offset += sizeof(ulong))
        {
            state = BitOperations.Crc32C(state, ReadUInt64(ref Unsafe.Add(ref start, offset)));
        }

        if (length - offset >= sizeof(uint))
        {
            uint word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, offset));
            state = BitOperations.Crc32C(state, BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word));
            offset += sizeof(uint);
        }

        for (; offset < length; offset++)
        {
            state = BitOperations.Crc32C(state, Unsafe.Add(ref start, offset));
        }

        return state;
    }

    /// <summary>
    /// Folds <paramref name="count"/> zero bytes into an incremental CRC computation, as
    /// <see cref="Append"/> over that many zeros would.
    /// </summary>
    internal static uint AppendZeros(uint state, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (; count >= sizeof(ulong); count -= sizeof(ulong))
        {
            state = BitOperations.Crc32C(state, 0UL);
        }

        for (; count > 0; count--)
        {
            state = BitOperations.Crc32C(state, (byte)0);
        }

        return state;
    }

    /// <summary>
    /// Completes an incremental CRC computation.
    /// </summary>
    internal static uint Finalize(uint state) => ~state;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ref byte source)
    {
        ulong word = Unsafe.ReadUnaligned<ulong>(ref source);
        return BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
    }
}
