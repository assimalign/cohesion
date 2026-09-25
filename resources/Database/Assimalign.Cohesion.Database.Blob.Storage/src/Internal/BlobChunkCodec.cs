using System;
using System.Buffers.Binary;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Storage.Internal;

internal static class BlobChunkCodec
{
    internal const int HeaderSize = 28;
    internal const int PayloadSize = SlottedPage.MaxRecordSize - HeaderSize;

    internal static byte[] Encode(ReadOnlySpan<byte> content, TransactionSequence writer)
    {
        var record = new byte[HeaderSize + content.Length];
        RecordVersionStamp.WriteWriter(record, writer);
        record[16] = 3;
        record[17] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), checked((ushort)content.Length));
        content.CopyTo(record.AsSpan(HeaderSize));
        return record;
    }

    internal static (ulong Next, ReadOnlyMemory<byte> Payload) Decode(ReadOnlyMemory<byte> bytes, long remaining)
    {
        if (bytes.Length <= HeaderSize || bytes.Span[16] != 3 || bytes.Span[17] != 1)
        {
            throw new StorageCorruptionException("Invalid blob chunk header.");
        }

        int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span[18..]);
        ulong next = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span[20..]);
        if (size != bytes.Length - HeaderSize || size > PayloadSize || size > remaining || (size == remaining) != (next == 0))
        {
            throw new StorageCorruptionException("Blob chunk length or next location disagrees with catalog length.");
        }

        return (next, bytes[HeaderSize..]);
    }

    internal static void WriteNext(Span<byte> record, ulong next)
        => BinaryPrimitives.WriteUInt64LittleEndian(record[20..], next);
}

internal static class BlobContentChecksum
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
