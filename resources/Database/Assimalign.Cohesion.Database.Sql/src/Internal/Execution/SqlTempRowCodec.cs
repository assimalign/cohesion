using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Private, versioned output tuples; representation is independent of sort identity.</summary>
internal static class SqlTempRowCodec
{
    private const byte version = 2;
    // Parameters can also deliver these CLR primitives without SqlValue coercion.
    private const byte byteTag = 20, ushortTag = 21, uintTag = 22, ulongTag = 23, charTag = 24;

    private const byte utf8StringTag = 25, utf8CharacterTag = 26;

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
                string text => TextLength(text),
                SqlCharacter character => TextLength(character.Value),
                byte[] bytes when !SqlBinaryValue.IsApplicationValue(bytes) && bytes.Length <= int.MaxValue - 4 => 4 + bytes.Length,
                _ => -1,
            };
            if (payload < 0 || (size += 1L + payload) > int.MaxValue) { length = 0; return false; }
        }
        length = (int)size;
        return true;
    }

    // Decoding creates separate objects even when input cells alias. Charge UTF-16 text,
    // CHAR's padded and trimmed strings, aligned objects, and the materialized row wrapper.
    internal static long DecodedBytes(ReadOnlySpan<object?> row)
    {
        // Array header (24) plus SqlMaterializedRow (32, including the base capability flag).
        long bytes = 56L + 8L * row.Length;
        foreach (object? value in row)
        {
            bytes += value switch
            {
                null => 0,
                string text => Align(22L + 2L * text.Length),
                SqlCharacter text => 32L + Align(22L + 2L * text.Value.Length) + Align(22L + 2L * text.Text.Length),
                byte[] binary => Align(24L + binary.Length),
                decimal or Guid or DateTimeOffset => 32,
                _ => 24,
            };
        }
        return bytes;
    }

    private static long Align(long bytes) => (bytes + 7) & ~7L;

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
                case string item:
                    int textBytes = WriteText(item, target, out bool utf8Text);
                    if (utf8Text) { destination[offset - 1] = utf8StringTag; }
                    offset += textBytes; break;
                case SqlCharacter item:
                    int characterBytes = WriteText(item.Value, target, out bool utf8Character);
                    if (utf8Character) { destination[offset - 1] = utf8CharacterTag; }
                    offset += characterBytes; break;
            }
        }
        written = offset;
        return true;
    }

    private static bool TryGetUtf8Length(string text, out int length)
    {
        long size = 0;
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out int consumed) != OperationStatus.Done)
            { length = 0; return false; }
            size += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }
        length = size <= int.MaxValue - 4 ? (int)size : -1;
        return true;
    }

    private static int TextLength(string text)
    {
        if (TryGetUtf8Length(text, out int length)) { return length < 0 ? -1 : 4 + length; }
        return text.Length <= (int.MaxValue - 4) / 2 ? 4 + text.Length * 2 : -1;
    }

    private static int WriteText(string text, Span<byte> target, out bool utf8)
    {
        utf8 = TryGetUtf8Length(text, out _);
        if (utf8)
        {
            Utf8.FromUtf16(text, target[4..], out _, out int written, replaceInvalidSequences: false);
            BinaryPrimitives.WriteInt32LittleEndian(target, written);
            return 4 + written;
        }
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
                (byte)DatabaseType.Null => 0,
                (byte)DatabaseType.Boolean or (byte)DatabaseType.Int8 or byteTag => 1,
                (byte)DatabaseType.Int16 or ushortTag or charTag => 2,
                (byte)DatabaseType.Int32 or (byte)DatabaseType.Float32 or (byte)DatabaseType.Date or uintTag => 4,
                (byte)DatabaseType.Int64 or (byte)DatabaseType.Float64 or (byte)DatabaseType.Time
                    or (byte)DatabaseType.DateTime or (byte)DatabaseType.TimeSpan or ulongTag => 8,
                (byte)DatabaseType.DateTimeOffset => 10,
                (byte)DatabaseType.Decimal or (byte)DatabaseType.Guid => 16,
                (byte)DatabaseType.String or (byte)DatabaseType.Binary or (byte)DatabaseType.Character
                    or utf8StringTag or utf8CharacterTag => -1,
                _ => -2,
            };
            if (size == -2) { return false; }
            if (size == -1)
            {
                if (bytes.Length - offset < 4) { return false; }
                size = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); offset += 4;
                if (size < 0 || size > bytes.Length - offset || (tag is (byte)DatabaseType.String or (byte)DatabaseType.Character) && (size & 1) != 0) { return false; }
                if (tag == (byte)DatabaseType.Binary)
                {
                    var binary = new byte[size];
                    bytes.Slice(offset, size).CopyTo(binary); result[i] = binary;
                }
                else
                {
                    var sourceText = bytes.Slice(offset, size);
                    string text;
                    if (tag is utf8StringTag or utf8CharacterTag)
                    {
                        if (!Utf8.IsValid(sourceText)) { return false; }
                        text = Encoding.UTF8.GetString(sourceText);
                    }
                    else
                    {
                        text = string.Create(size / 2, sourceText, static (chars, source) =>
                        {
                            for (int j = 0; j < chars.Length; j++)
                            { chars[j] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source[(j * 2)..]); }
                        });
                    }
                    result[i] = tag is (byte)DatabaseType.Character or utf8CharacterTag ? new SqlCharacter(text) : text;
                }
                offset += size; continue;
            }
            if (size > bytes.Length - offset) { return false; }
            var source = bytes.Slice(offset, size); offset += size;
            switch (tag)
            {
                case (byte)DatabaseType.Null: break;
                case (byte)DatabaseType.Boolean: if (source[0] > 1) { return false; } result[i] = source[0] != 0; break;
                case (byte)DatabaseType.Int8: result[i] = unchecked((sbyte)source[0]); break;
                case (byte)DatabaseType.Int16: result[i] = BinaryPrimitives.ReadInt16LittleEndian(source); break;
                case (byte)DatabaseType.Int32: result[i] = BinaryPrimitives.ReadInt32LittleEndian(source); break;
                case (byte)DatabaseType.Int64: result[i] = BinaryPrimitives.ReadInt64LittleEndian(source); break;
                case (byte)DatabaseType.Float32: result[i] = BinaryPrimitives.ReadSingleLittleEndian(source); break;
                case (byte)DatabaseType.Float64: result[i] = BinaryPrimitives.ReadDoubleLittleEndian(source); break;
                case (byte)DatabaseType.Decimal:
                    int flags = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
                    if ((flags & 0x7f00ffff) != 0 || ((flags >> 16) & 255) > 28) { return false; }
                    result[i] = new decimal(BinaryPrimitives.ReadInt32LittleEndian(source), BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
                        BinaryPrimitives.ReadInt32LittleEndian(source[8..]), flags < 0, (byte)(flags >> 16)); break;
                case (byte)DatabaseType.Date:
                    int day = BinaryPrimitives.ReadInt32LittleEndian(source);
                    if ((uint)day > DateOnly.MaxValue.DayNumber) { return false; }
                    result[i] = DateOnly.FromDayNumber(day); break;
                case (byte)DatabaseType.Time:
                    long time = BinaryPrimitives.ReadInt64LittleEndian(source);
                    if ((ulong)time >= (ulong)TimeSpan.TicksPerDay) { return false; }
                    result[i] = new TimeOnly(time); break;
                case (byte)DatabaseType.DateTime:
                    ulong date = BinaryPrimitives.ReadUInt64LittleEndian(source);
                    if ((date & 0x3fffffffffffffff) > (ulong)DateTime.MaxValue.Ticks) { return false; }
                    result[i] = Unsafe.BitCast<ulong, DateTime>(date); break;
                case (byte)DatabaseType.DateTimeOffset:
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(source);
                    short minutes = BinaryPrimitives.ReadInt16LittleEndian(source[8..]);
                    long utc = ticks - minutes * TimeSpan.TicksPerMinute;
                    if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks || minutes is < -840 or > 840 || (ulong)utc > (ulong)DateTime.MaxValue.Ticks) { return false; }
                    result[i] = new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes)); break;
                case (byte)DatabaseType.TimeSpan: result[i] = new TimeSpan(BinaryPrimitives.ReadInt64LittleEndian(source)); break;
                case (byte)DatabaseType.Guid: result[i] = new Guid(source); break;
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
