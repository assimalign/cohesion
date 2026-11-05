using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Private, versioned output tuples; representation is independent of sort identity.</summary>
internal static class SqlTempRowCodec
{
    private const byte version = 1;
    // Parameters can also deliver these CLR primitives without SqlValue coercion.
    private const byte byteTag = 20, ushortTag = 21, uintTag = 22, ulongTag = 23, charTag = 24;

    internal static bool TryGetLength(ReadOnlySpan<object?> row, out int length)
    {
        long size = 5;
        foreach (object? value in row)
        {
            int payload = value switch
            {
                null => 0,
                bool or sbyte or byte => 1,
                short or ushort or char => 2,
                int or uint or float or DateOnly => 4,
                long or ulong or double or TimeOnly or DateTime or TimeSpan => 8,
                DateTimeOffset => 10,
                decimal or Guid => 16,
                string text when text.Length <= (int.MaxValue - 4) / 2 => 4 + text.Length * 2,
                SqlCharacter character when character.Value.Length <= (int.MaxValue - 4) / 2 => 4 + character.Value.Length * 2,
                byte[] bytes when bytes.Length <= int.MaxValue - 4 => 4 + bytes.Length,
                _ => -1,
            };
            if (payload < 0 || (size += 1L + payload) > int.MaxValue) { length = 0; return false; }
        }
        length = (int)size;
        return true;
    }

    internal static bool TryEncode(ReadOnlySpan<object?> row, out byte[] bytes)
    {
        bytes = [];
        if (!TryGetLength(row, out int length)) { return false; }
        bytes = new byte[length];
        return TryWrite(row, bytes, out _);
    }

    /// <summary>Writes into caller-owned scratch with no allocation, including for strings.</summary>
    internal static bool TryWrite(ReadOnlySpan<object?> row, Span<byte> destination, out int written)
    {
        written = 0;
        if (!TryGetLength(row, out int length) || destination.Length < length) { return false; }
        destination[0] = version;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], row.Length);
        int offset = 5;
        Span<int> parts = stackalloc int[4];
        foreach (object? value in row)
        {
            byte tag = value switch
            {
                null => 0, bool => (byte)DatabaseType.Boolean, sbyte => (byte)DatabaseType.Int8,
                short => (byte)DatabaseType.Int16, int => (byte)DatabaseType.Int32, long => (byte)DatabaseType.Int64,
                float => (byte)DatabaseType.Float32, double => (byte)DatabaseType.Float64, decimal => (byte)DatabaseType.Decimal,
                string => (byte)DatabaseType.String, byte[] => (byte)DatabaseType.Binary,
                DateOnly => (byte)DatabaseType.Date, TimeOnly => (byte)DatabaseType.Time,
                DateTime => (byte)DatabaseType.DateTime, DateTimeOffset => (byte)DatabaseType.DateTimeOffset,
                TimeSpan => (byte)DatabaseType.TimeSpan, Guid => (byte)DatabaseType.Guid,
                SqlCharacter => (byte)DatabaseType.Character, byte => byteTag, ushort => ushortTag,
                uint => uintTag, ulong => ulongTag, char => charTag, _ => 0,
            };
            destination[offset++] = tag;
            Span<byte> target = destination[offset..];
            switch (value)
            {
                case null: break;
                case bool item: target[0] = item ? (byte)1 : (byte)0; offset++; break;
                case sbyte item: target[0] = unchecked((byte)item); offset++; break;
                case byte item: target[0] = item; offset++; break;
                case short item: BinaryPrimitives.WriteInt16LittleEndian(target, item); offset += 2; break;
                case ushort item: BinaryPrimitives.WriteUInt16LittleEndian(target, item); offset += 2; break;
                case char item: BinaryPrimitives.WriteUInt16LittleEndian(target, item); offset += 2; break;
                case int item: BinaryPrimitives.WriteInt32LittleEndian(target, item); offset += 4; break;
                case uint item: BinaryPrimitives.WriteUInt32LittleEndian(target, item); offset += 4; break;
                case float item: BinaryPrimitives.WriteSingleLittleEndian(target, item); offset += 4; break;
                case DateOnly item: BinaryPrimitives.WriteInt32LittleEndian(target, item.DayNumber); offset += 4; break;
                case long item: BinaryPrimitives.WriteInt64LittleEndian(target, item); offset += 8; break;
                case ulong item: BinaryPrimitives.WriteUInt64LittleEndian(target, item); offset += 8; break;
                case double item: BinaryPrimitives.WriteDoubleLittleEndian(target, item); offset += 8; break;
                case TimeOnly item: BinaryPrimitives.WriteInt64LittleEndian(target, item.Ticks); offset += 8; break;
                // Preserve all DateTime bits, including the ambiguous-local flag, without timezone conversion.
                case DateTime item: BinaryPrimitives.WriteUInt64LittleEndian(target, Unsafe.BitCast<DateTime, ulong>(item)); offset += 8; break;
                case TimeSpan item: BinaryPrimitives.WriteInt64LittleEndian(target, item.Ticks); offset += 8; break;
                case DateTimeOffset item:
                    BinaryPrimitives.WriteInt64LittleEndian(target, item.Ticks);
                    BinaryPrimitives.WriteInt16LittleEndian(target[8..], (short)item.Offset.TotalMinutes);
                    offset += 10; break;
                case decimal item:
                    decimal.GetBits(item, parts);
                    for (int i = 0; i < 4; i++) { BinaryPrimitives.WriteInt32LittleEndian(target[(i * 4)..], parts[i]); }
                    offset += 16; break;
                case Guid item: item.TryWriteBytes(target); offset += 16; break;
                case byte[] item:
                    BinaryPrimitives.WriteInt32LittleEndian(target, item.Length);
                    item.CopyTo(target[4..]); offset += 4 + item.Length; break;
                case string item: offset += WriteText(item, target); break;
                case SqlCharacter item: offset += WriteText(item.Value, target); break;
            }
        }
        written = offset;
        return true;
    }

    private static int WriteText(string text, Span<byte> target)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target, text.Length * 2);
        for (int i = 0; i < text.Length; i++) { BinaryPrimitives.WriteUInt16LittleEndian(target[(4 + i * 2)..], text[i]); }
        return 4 + text.Length * 2;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out object?[] row)
    {
        row = [];
        if (bytes.Length < 5 || bytes[0] != version) { return false; }
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (count < 0 || count > bytes.Length - 5) { return false; }
        var result = new object?[count];
        int offset = 5;
        for (int i = 0; i < count; i++)
        {
            if (offset == bytes.Length) { return false; }
            byte tag = bytes[offset++];
            int size = tag switch
            {
                0 => 0, 1 or 2 or byteTag => 1, 3 or ushortTag or charTag => 2,
                4 or 6 or 11 or uintTag => 4, 5 or 7 or 12 or 13 or 15 or ulongTag => 8,
                14 => 10, 8 or 16 => 16, 9 or 10 or 19 => -1, _ => -2,
            };
            if (size == -2) { return false; }
            if (size == -1)
            {
                if (bytes.Length - offset < 4) { return false; }
                size = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); offset += 4;
                if (size < 0 || size > bytes.Length - offset || tag != 10 && (size & 1) != 0) { return false; }
                if (tag == 10)
                {
                    var binary = new byte[size];
                    bytes.Slice(offset, size).CopyTo(binary); result[i] = binary;
                }
                else
                {
                    var chars = new char[size / 2];
                    for (int j = 0; j < chars.Length; j++) { chars[j] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + j * 2)..]); }
                    string text = new(chars);
                    result[i] = tag == 19 ? new SqlCharacter(text) : text;
                }
                offset += size; continue;
            }
            if (size > bytes.Length - offset) { return false; }
            var source = bytes.Slice(offset, size); offset += size;
            switch (tag)
            {
                case 0: break;
                case 1: if (source[0] > 1) { return false; } result[i] = source[0] != 0; break;
                case 2: result[i] = unchecked((sbyte)source[0]); break;
                case 3: result[i] = BinaryPrimitives.ReadInt16LittleEndian(source); break;
                case 4: result[i] = BinaryPrimitives.ReadInt32LittleEndian(source); break;
                case 5: result[i] = BinaryPrimitives.ReadInt64LittleEndian(source); break;
                case 6: result[i] = BinaryPrimitives.ReadSingleLittleEndian(source); break;
                case 7: result[i] = BinaryPrimitives.ReadDoubleLittleEndian(source); break;
                case 8:
                    int flags = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
                    if ((flags & 0x7f00ffff) != 0 || ((flags >> 16) & 255) > 28) { return false; }
                    result[i] = new decimal(BinaryPrimitives.ReadInt32LittleEndian(source), BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
                        BinaryPrimitives.ReadInt32LittleEndian(source[8..]), flags < 0, (byte)(flags >> 16)); break;
                case 11:
                    int day = BinaryPrimitives.ReadInt32LittleEndian(source);
                    if ((uint)day > DateOnly.MaxValue.DayNumber) { return false; }
                    result[i] = DateOnly.FromDayNumber(day); break;
                case 12:
                    long time = BinaryPrimitives.ReadInt64LittleEndian(source);
                    if ((ulong)time >= (ulong)TimeSpan.TicksPerDay) { return false; }
                    result[i] = new TimeOnly(time); break;
                case 13:
                    ulong date = BinaryPrimitives.ReadUInt64LittleEndian(source);
                    if ((date & 0x3fffffffffffffff) > (ulong)DateTime.MaxValue.Ticks) { return false; }
                    result[i] = Unsafe.BitCast<ulong, DateTime>(date); break;
                case 14:
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(source);
                    short minutes = BinaryPrimitives.ReadInt16LittleEndian(source[8..]);
                    long utc = ticks - minutes * TimeSpan.TicksPerMinute;
                    if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks || minutes is < -840 or > 840 || (ulong)utc > (ulong)DateTime.MaxValue.Ticks) { return false; }
                    result[i] = new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes)); break;
                case 15: result[i] = new TimeSpan(BinaryPrimitives.ReadInt64LittleEndian(source)); break;
                case 16: result[i] = new Guid(source); break;
                case byteTag: result[i] = source[0]; break;
                case ushortTag: result[i] = BinaryPrimitives.ReadUInt16LittleEndian(source); break;
                case uintTag: result[i] = BinaryPrimitives.ReadUInt32LittleEndian(source); break;
                case ulongTag: result[i] = BinaryPrimitives.ReadUInt64LittleEndian(source); break;
                case charTag: result[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source); break;
            }
        }
        if (offset != bytes.Length) { return false; }
        row = result;
        return true;
    }
}
