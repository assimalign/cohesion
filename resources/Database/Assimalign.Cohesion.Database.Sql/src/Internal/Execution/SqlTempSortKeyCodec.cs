using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Prefix-free components ordered exactly like the collector's comparable SQL values.</summary>
internal static class SqlTempSortKeyCodec
{
    private static readonly BigInteger[] _powersOfFive = CreatePowersOfFive();

    // The caller supplies one FINAL resolved collation per column for the entire sort.
    // A deferred ORDER BY follows the last qualifying row in SqlRowCollector: resolve that
    // snapshot before encoding ANY keys, or refuse spill. Per-row snapshots cannot be mixed.
    internal readonly record struct Column(bool IsDescending, bool NullsFirst, Collation Collation);

    internal static bool TryEncode(ReadOnlySpan<object?> keys, ReadOnlySpan<Column> columns, out byte[] bytes)
    {
        bytes = [];
        if (keys.Length != columns.Length) { return false; }
        var writer = new ArrayBufferWriter<byte>();
        int[]? rented = null;
        Span<int> ends = keys.Length <= 64 ? stackalloc int[keys.Length] : (rented = ArrayPool<int>.Shared.Rent(keys.Length));
        try
        {
            for (int i = 0; i < keys.Length; i++)
            {
                var column = columns[i];
                if (column.Collation is null || !column.Collation.IsIndexBacked) { return false; }
                if (keys[i] is null) { Append(writer, column.NullsFirst ? (byte)0 : (byte)255); }
                else
                {
                    // NULL placement is independent of DESC. Values start with an interior marker.
                    Append(writer, 128);
                    if (!TryAppendValue(writer, keys[i]!, column.Collation)) { return false; }
                }
                ends[i] = writer.WrittenCount;
            }
            bytes = writer.WrittenSpan.ToArray();
            for (int i = 0, start = 0; i < keys.Length; start = ends[i++])
            {
                if (keys[i] is null || !columns[i].IsDescending) { continue; }
                // Complement the COMPLETE prefix-free component, including its terminator.
                for (int j = start; j < ends[i]; j++) { bytes[j] = (byte)~bytes[j]; }
            }
            return true;
        }
        finally { if (rented is not null) { ArrayPool<int>.Shared.Return(rented); } }
    }

    private static bool TryAppendValue(ArrayBufferWriter<byte> writer, object value, Collation collation)
    {
        switch (value)
        {
            case sbyte or short or int or long or float or double or decimal:
                AppendNumber(writer, value); return true;
            case string text: return TryAppendText(writer, text, collation);
            case SqlCharacter character: return TryAppendText(writer, character.Text, collation);
            case byte[] binary: AppendEscaped(writer, binary); return true;
            case bool flag: Append(writer, flag ? (byte)1 : (byte)0); return true;
            case DateOnly date: AppendInt64(writer, date.ToDateTime(TimeOnly.MinValue).Ticks); return true;
            case DateTime timestamp: AppendInt64(writer, timestamp.Ticks); return true;
            case DateTimeOffset instant: AppendInt64(writer, instant.UtcTicks); return true;
            case TimeOnly time: AppendInt64(writer, time.Ticks); return true;
            case TimeSpan interval: AppendInt64(writer, interval.Ticks); return true;
            case Guid guid:
                guid.TryWriteBytes(writer.GetSpan(16), bigEndian: true, out _); writer.Advance(16); return true;
            // These parameters compare only with the same CLR type, per SqlValueComparer.
            case byte number: Append(writer, number); return true;
            case ushort number: AppendUInt64(writer, number); return true;
            case uint number: AppendUInt64(writer, number); return true;
            case ulong number: AppendUInt64(writer, number); return true;
            case char character: AppendUInt64(writer, character); return true;
            default: return false;
        }
    }

    private static bool TryAppendText(ArrayBufferWriter<byte> writer, string text, Collation collation)
    {
        // The row codec preserves arbitrary UTF-16. The comparator and stable collations
        // reject malformed UTF-16; never replace surrogates to manufacture a sortable key.
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done) { return false; }
            remaining = remaining[consumed..];
        }
        AppendEscaped(writer, collation.GetSortKey(text));
        return true;
    }

    private static void AppendNumber(ArrayBufferWriter<byte> writer, object value)
    {
        BigInteger coefficient;
        int power;
        bool negative;
        if (value is float or double)
        {
            double number = value is float single ? single : (double)value;
            if (double.IsNaN(number)) { Append(writer, 0); return; }
            if (double.IsNegativeInfinity(number)) { Append(writer, 1); return; }
            if (double.IsPositiveInfinity(number)) { Append(writer, 5); return; }
            ulong bits = BitConverter.DoubleToUInt64Bits(number);
            int exponent = (int)((bits >> 52) & 2047);
            ulong significand = bits & 0x000fffffffffffff;
            if (exponent != 0) { significand |= 1UL << 52; }
            negative = (bits >> 63) != 0;
            coefficient = significand * _powersOfFive[28];
            power = (exponent == 0 ? -1074 : exponent - 1075) + 28;
        }
        else
        {
            decimal number = value switch { sbyte n => n, short n => n, int n => n, long n => n, _ => (decimal)value };
            Span<int> parts = stackalloc int[4];
            decimal.GetBits(number, parts);
            int scale = (parts[3] >> 16) & 255;
            negative = parts[3] < 0;
            coefficient = ((BigInteger)(uint)parts[0] + ((BigInteger)(uint)parts[1] << 32) + ((BigInteger)(uint)parts[2] << 64))
                * _powersOfFive[28 - scale];
            power = 28 - scale;
        }
        if (coefficient.IsZero) { Append(writer, 3); return; }
        Append(writer, negative ? (byte)2 : (byte)4);
        // All numbers are scaled by the SAME 10^28. The remaining denominator is a
        // power of two. Align to base 256, encode its signed exponent, and remove trailing
        // zero digits. Equal represented values now have identical exponent and digits.
        int remainder = power & 7;
        coefficient <<= remainder;
        int bytePower = (power - remainder) / 8;
        Span<byte> digits = stackalloc byte[32];
        coefficient.TryWriteBytes(digits, out int count, isUnsigned: true, isBigEndian: true);
        int exponent256 = count + bytePower;
        while (digits[count - 1] == 0) { count--; }
        var exponentBytes = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(exponentBytes, (ushort)(exponent256 + 32768));
        if (negative) { exponentBytes[0] = (byte)~exponentBytes[0]; exponentBytes[1] = (byte)~exponentBytes[1]; }
        writer.Advance(2);
        AppendEscaped(writer, digits[..count], negative);
    }

    private static void AppendEscaped(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value, bool complement = false)
    {
        foreach (byte item in value)
        {
            Append(writer, complement ? (byte)~item : item);
            if (item == 0) { Append(writer, complement ? (byte)0 : (byte)255); }
        }
        Append(writer, complement ? (byte)255 : (byte)0);
        Append(writer, complement ? (byte)255 : (byte)0);
    }

    private static void AppendInt64(ArrayBufferWriter<byte> writer, long value) => AppendUInt64(writer, unchecked((ulong)value) ^ 0x8000000000000000);
    private static void AppendUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(writer.GetSpan(8), value); writer.Advance(8);
    }
    private static void Append(ArrayBufferWriter<byte> writer, byte value) { writer.GetSpan(1)[0] = value; writer.Advance(1); }
    private static BigInteger[] CreatePowersOfFive()
    {
        var powers = new BigInteger[29]; powers[0] = BigInteger.One;
        for (int i = 1; i < powers.Length; i++) { powers[i] = powers[i - 1] * 5; }
        return powers;
    }
}
