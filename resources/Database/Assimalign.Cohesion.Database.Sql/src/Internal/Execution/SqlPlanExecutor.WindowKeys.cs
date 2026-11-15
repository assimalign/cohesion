using System;
using System.Buffers;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Same prefix-free components as SqlTempSortKeyCodec, into a capped buffer.
    // A repeated wide key must refuse before an expanding writer duplicates it.
    internal static bool TryEncodeWindowKeys(ReadOnlySpan<object?> values,
        ReadOnlySpan<SqlTempSortKeyCodec.Column> columns, Span<byte> families,
        Span<byte> destination, out byte[] encoded, out bool compatible)
    {
        encoded = []; compatible = true;
        if (values.Length != columns.Length || families.Length != columns.Length) { compatible = false; return false; }
        int position = 0;
        Span<byte> utf8 = stackalloc byte[4];
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i]; var column = columns[i];
            if (value is null)
            {
                if (!AppendWindowKeyByte(destination, ref position, column.NullsFirst ? (byte)0 : (byte)255, false)) { return false; }
                continue;
            }
            if (value is not (string or SqlCharacter or byte[]))
            {
                if (!SqlTempSortKeyCodec.TryEncode(values.Slice(i, 1), columns.Slice(i, 1), families.Slice(i, 1), out var scalar))
                { compatible = false; return false; }
                if (scalar.Length > destination.Length - position) { return false; }
                scalar.AsSpan().CopyTo(destination[position..]); position += scalar.Length; continue;
            }
            byte family = SqlValueComparer.ComparisonFamily(value);
            if (family == 0 || families[i] != 0 && families[i] != family) { compatible = false; return false; }
            families[i] = family;
            if (!AppendWindowKeyByte(destination, ref position, 128, column.IsDescending)) { return false; }
            if (value is byte[] binary)
            {
                if (SqlBinaryValue.IsApplicationValue(binary)) { compatible = false; return false; }
                if (!AppendWindowKeyBytes(destination, ref position, binary, column.IsDescending)) { return false; }
            }
            else
            {
                if (column.Collation is null || !column.Collation.IsIndexBacked) { compatible = false; return false; }
                ReadOnlySpan<char> remaining = value is string text ? text : ((SqlCharacter)value).Text;
                while (!remaining.IsEmpty)
                {
                    if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done) { compatible = false; return false; }
                    remaining = remaining[consumed..];
                    if (column.Collation.Id == Collation.Binary.Id)
                    {
                        int count = rune.EncodeToUtf8(utf8);
                        if (!AppendWindowKeyBytes(destination, ref position, utf8[..count], column.IsDescending)) { return false; }
                    }
                    else
                    {
                        // Index-backed normalization is rune-local (Collation.Normalize).
                        // Even canonical decomposition can therefore use bounded fragments.
                        var fragment = column.Collation.GetSortKey(rune.ToString());
                        if (!AppendWindowKeyBytes(destination, ref position, fragment, column.IsDescending)) { return false; }
                    }
                }
            }
            if (!AppendWindowKeyByte(destination, ref position, 0, column.IsDescending)
                || !AppendWindowKeyByte(destination, ref position, 0, column.IsDescending)) { return false; }
        }
        encoded = destination[..position].ToArray(); return true;
    }

    private static bool AppendWindowKeyBytes(Span<byte> destination, ref int position, ReadOnlySpan<byte> value, bool descending)
    {
        foreach (byte item in value)
        {
            if (!AppendWindowKeyByte(destination, ref position, item, descending)
                || item == 0 && !AppendWindowKeyByte(destination, ref position, 255, descending)) { return false; }
        }
        return true;
    }

    private static bool AppendWindowKeyByte(Span<byte> destination, ref int position, byte value, bool descending)
    {
        if (position == destination.Length) { return false; }
        destination[position++] = descending ? (byte)~value : value; return true;
    }
}
