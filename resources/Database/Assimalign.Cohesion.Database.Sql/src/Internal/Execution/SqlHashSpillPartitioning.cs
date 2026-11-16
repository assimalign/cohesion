using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Shared by hash operators: three fresh bits per level, with a bounded replay stack.
internal static class SqlHashSpillPartitioning
{
    internal const int Count = 8;
    internal const int MaximumDepth = 8;
    internal static uint HashAt(int hash, int depth) => unchecked((uint)hash) >> (3 * depth);
    internal static uint Prefix(uint hash, int depth) => hash & ((1U << (3 * depth)) - 1);
    internal static void WriteOrdinal(Span<byte> key, long ordinal) => BinaryPrimitives.WriteInt64BigEndian(key, ordinal);
    internal static byte[] OrdinalKey(long ordinal, byte? suffix = null)
    {
        var key = new byte[suffix.HasValue ? 9 : 8];
        WriteOrdinal(key, ordinal);
        if (suffix is { } kind) { key[8] = kind; }
        return key;
    }
}
