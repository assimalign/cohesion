using System;
using System.Buffers;
using System.Buffers.Binary;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>SQL's component order transform over the shared ascending, self-delimiting codec.</summary>
internal static class SqlIndexKeyEncoding
{
    internal static void Transform(Span<byte> component, SqlCatalogIndexKeyOrder order)
    {
        // SQL orders NaN below every other numeric and equates signed zeros.
        // The shared row codec deliberately preserves IEEE value representation.
        // Canonicalize only keys, before direction is applied.
        if (component[0] == (byte)DatabaseType.Float32)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(component[1..]);
            if (value == 0xFFC00000) { BinaryPrimitives.WriteUInt32BigEndian(component[1..], 0); }
            else if (value == 0x7FFFFFFF) { BinaryPrimitives.WriteUInt32BigEndian(component[1..], 0x80000000); }
        }
        else if (component[0] == (byte)DatabaseType.Float64)
        {
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(component[1..]);
            if (value == 0xFFF8000000000000) { BinaryPrimitives.WriteUInt64BigEndian(component[1..], 0); }
            else if (value == 0x7FFFFFFFFFFFFFFF) { BinaryPrimitives.WriteUInt64BigEndian(component[1..], 0x8000000000000000); }
        }
        // The NULL marker is independent of value direction. All non-NULL type tags
        // are strictly between 0 and 255, both before and after complementing.
        if (component[0] == 0)
        {
            component[0] = ((int)order & 1) != 0 ? (byte)0 : (byte)255;
        }
        else if (((int)order & 2) != 0)
        {
            // Complement the complete component, including variable-width terminators.
            // Components of one storage type are prefix-free; concatenation therefore
            // reverses only this column, including strings that prefix another string.
            for (int i = 0; i < component.Length; i++) { component[i] = (byte)~component[i]; }
        }
    }

    // Logical bounds are ascending SQL comparisons. Translate them to physical
    // order only after encoding every equality-prefix component.
    internal static IndexKeyRange BuildSeekRange(SqlCatalogTable table, SqlIndexSeekPath seek, int[] indexOrdinals, Collation defaultCollation)
    {
        // A proven partial-index subset scan (#1630, SQ-P2) has no prefix and no bounds:
        // it reads the whole tree, NULL keys included.
        if (seek.EqualityValues.Count == 0 && seek.Lower is null && seek.Upper is null) { return IndexKeyRange.All; }
        var prefixWriter = new DatabaseKeyWriter();
        int[]? rented = null;
        Span<int> ends = seek.EqualityValues.Count <= 64 ? stackalloc int[seek.EqualityValues.Count]
            : (rented = ArrayPool<int>.Shared.Rent(seek.EqualityValues.Count));
        byte[] prefix;
        try
        {
            for (int i = 0; i < seek.EqualityValues.Count; i++)
            {
                int ordinal = indexOrdinals[i];
                SqlRowCodec.AppendKeyValue(prefixWriter, table.Columns[ordinal].Type.Type, seek.EqualityValues[i],
                    table.Columns[ordinal].Collation ?? defaultCollation);
                ends[i] = prefixWriter.Length;
            }

            prefix = prefixWriter.ToArray();
            for (int i = 0, offset = 0; i < seek.EqualityValues.Count; offset = ends[i++])
            {
                Transform(prefix.AsSpan(offset, ends[i] - offset), seek.Index.KeyOrders[i]);
            }
        }
        finally
        {
            if (rented is not null) { ArrayPool<int>.Shared.Return(rented); }
        }

        if (seek.Lower is null && seek.Upper is null)
        {
            return new IndexKeyRange(
                new IndexKey(prefix),
                PrefixSuccessor(prefix) is { } successor ? new IndexKey(successor) : null,
                IsStartInclusive: true,
                IsEndInclusive: false);
        }

        int rangeOrdinal = indexOrdinals[seek.EqualityValues.Count];
        var rangeType = table.Columns[rangeOrdinal].Type.Type;
        var rangeCollation = table.Columns[rangeOrdinal].Collation ?? defaultCollation;

        IndexKey? start = seek.EqualityValues.Count > 0 ? new IndexKey(prefix) : null;
        bool startInclusive = true;
        IndexKey? end = seek.EqualityValues.Count > 0 && PrefixSuccessor(prefix) is { } prefixEnd ? new IndexKey(prefixEnd) : null;
        bool endInclusive = false;

        var rangeOrder = seek.Index.KeyOrders[seek.EqualityValues.Count];
        bool descending = ((int)rangeOrder & 2) != 0;
        bool nullsFirst = ((int)rangeOrder & 1) != 0;
        if ((descending ? seek.Upper : seek.Lower) is { } lower)
        {
            byte[] lowerKey = AppendComponent(prefix, rangeType, lower.Value, rangeCollation, rangeOrder);

            // Exclusive lower: skip every composite key whose range component
            // equals the bound — start at the bound's prefix successor.
            start = lower.Inclusive
                ? new IndexKey(lowerKey)
                : PrefixSuccessor(lowerKey) is { } lowerSuccessor ? new IndexKey(lowerSuccessor) : null;
            startInclusive = true;
        }
        else if (nullsFirst)
        {
            // Comparison bounds never contain NULL. Skip its entire component
            // block, including every suffix, when this physical side is open.
            start = new IndexKey(AppendMarker(prefix, 0x01));
        }

        if ((descending ? seek.Lower : seek.Upper) is { } upper)
        {
            byte[] upperKey = AppendComponent(prefix, rangeType, upper.Value, rangeCollation, rangeOrder);

            // Inclusive upper: admit every composite key whose range component
            // equals the bound — end (exclusively) at the bound's successor.
            end = upper.Inclusive
                ? PrefixSuccessor(upperKey) is { } upperSuccessor ? new IndexKey(upperSuccessor) : null
                : new IndexKey(upperKey);
            endInclusive = false;
        }
        else if (!nullsFirst)
        {
            end = new IndexKey(AppendMarker(prefix, 0xFF));
        }

        return new IndexKeyRange(start, end, startInclusive, endInclusive);
    }

    private static byte[] AppendMarker(byte[] prefix, byte marker)
    {
        var combined = new byte[prefix.Length + 1];
        prefix.CopyTo(combined, 0);
        combined[^1] = marker;
        return combined;
    }

    private static byte[] AppendComponent(byte[] prefix, DatabaseType type, object? value, Collation collation, SqlCatalogIndexKeyOrder order)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, type, value, collation);
        byte[] component = writer.ToArray();
        Transform(component, order);

        var combined = new byte[prefix.Length + component.Length];
        prefix.CopyTo(combined, 0);
        component.CopyTo(combined, prefix.Length);
        return combined;
    }

    /// <summary>
    /// Computes the smallest byte string greater than every string with the
    /// given prefix, or null when none exists (an all-0xFF prefix): increment
    /// the last non-0xFF byte and truncate behind it.
    /// </summary>
    private static byte[]? PrefixSuccessor(byte[] prefix)
    {
        for (int i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != 0xFF)
            {
                var successor = new byte[i + 1];
                prefix.AsSpan(0, i + 1).CopyTo(successor);
                successor[i]++;
                return successor;
            }
        }

        return null;
    }
}
