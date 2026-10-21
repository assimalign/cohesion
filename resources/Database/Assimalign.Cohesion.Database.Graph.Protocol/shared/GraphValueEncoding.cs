using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule per design decision: linked encoding logic keeps its owning namespace in both assemblies.
namespace Assimalign.Cohesion.Database.Graph.Protocol.Internal;

/// <summary>Encodes Graph composite values, exact Decimal bits and scalar DatabaseValueCodec components.</summary>
/// <remarks>This format requires GraphProtocol.CompositeValues. It is never an index key format.
/// Lists decode to object arrays; maps decode to read-only ordinal string dictionaries.</remarks>
internal static class GraphValueEncoding
{
    /// <summary>The maximum nested container depth (a root value has depth zero).</summary>
    public const int MaxDepth = 64;
    /// <summary>The maximum total values, including the root, in one encoded value.</summary>
    public const int MaxValueCount = 65_536;
    /// <summary>The maximum encoded size of one value or row.</summary>
    public const int MaxPayloadLength = 16_777_216;

    private static readonly UTF8Encoding _utf8 = new(false, true);

    /// <summary>Encodes one scalar, list or map with explicit runtime type tags.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The complete encoded value.</returns>
    /// <exception cref="DatabaseTypeException">A type or format bound is unsupported.</exception>
    internal static byte[] Encode<TMessages>(object? value)
        where TMessages : struct, IGraphEncodingMessages
    {
        Measure<TMessages>(value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, _utf8, leaveOpen: true);
        int count = 0;
        try { Write<TMessages>(writer, value, 0, ref count, new DatabaseKeyWriter(32)); }
        catch (EncoderFallbackException error)
        { throw new DatabaseTypeException(TMessages.CompositeMalformed, error); }
        return stream.ToArray();
    }

    /// <summary>Decodes one bounded composite component.</summary>
    /// <param name="payload">The complete component.</param>
    /// <returns>The decoded value with scalar runtime types preserved.</returns>
    /// <exception cref="ProtocolException">The component is malformed or exceeds a bound.</exception>
    internal static object? Decode<TMessages>(ReadOnlySpan<byte> payload)
        where TMessages : struct, IGraphEncodingMessages
    {
        if (payload.Length > MaxPayloadLength) { throw new ProtocolException(TMessages.CompositeMalformed); }
        int count = 1;
        var remaining = payload;
        try
        {
            object? value = Read<TMessages>(ref remaining, 0, ref count);
            if (!remaining.IsEmpty) { throw new ProtocolException(TMessages.CompositeMalformed); }
            return value;
        }
        catch (Exception error) when (error is DatabaseTypeException or ArgumentException or OverflowException)
        { throw new ProtocolException(TMessages.CompositeMalformed, error); }
    }

    internal static int Measure<TMessages>(object? value)
        where TMessages : struct, IGraphEncodingMessages
    {
        int count = 0;
        try { return Measure<TMessages>(value, 0, ref count); }
        catch (EncoderFallbackException error) { throw new DatabaseTypeException(TMessages.CompositeMalformed, error); }
    }

    private static int Measure<TMessages>(object? value, int depth, ref int count)
        where TMessages : struct, IGraphEncodingMessages
    {
        if (depth > MaxDepth || ++count > MaxValueCount) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
        long size;
        if (value is IReadOnlyDictionary<string, object?> map)
        {
            if (map.Count > MaxValueCount - count) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
            size = 5;
            foreach (var entry in map)
            { size += 4L + _utf8.GetByteCount(entry.Key) + Measure<TMessages>(entry.Value, depth + 1, ref count); }
        }
        else if (value is IList list && value is not byte[])
        {
            if (list.Count > MaxValueCount - count) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
            size = 5;
            foreach (var child in list) { size += Measure<TMessages>(child, depth + 1, ref count); }
        }
        else { size = value is decimal ? 17 : ScalarLength<TMessages>(value) + (value is byte or ushort or uint or ulong ? 0 : 5L); }
        if (size > MaxPayloadLength) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
        return (int)size;
    }

    internal static int ScalarLength<TMessages>(object? value)
        where TMessages : struct, IGraphEncodingMessages
    {
        long size = value switch
        {
            null => 1, bool or sbyte or byte => 2, short or ushort => 3, int or float or uint => 5,
            long or double or ulong => 9,
            string text => 4L + _utf8.GetByteCount(text) + ZeroCount(text),
            byte[] bytes => 3L + bytes.Length + ZeroCount(bytes),
            GraphNode or GraphRelationship or GraphPath => 1, // In-process results may contain entities.
            decimal number => DecimalLength(number),
            DateOnly => 5, TimeOnly or TimeSpan => 9, DateTime => 10, DateTimeOffset => 11, Guid => 17,
            _ => throw new DatabaseTypeException(TMessages.CompositeRequired),
        };
        if (size > MaxPayloadLength) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
        return (int)size;
    }

    // Database.Types/Internal/KeyComponentEncoding.cs:238-278,347-376: sign, exponent,
    // significant decimal digits and terminator; measure without allocating an encoded value.
    private static int DecimalLength(decimal value)
    {
        if (value == 0m) { return 2; }
        Span<char> text = stackalloc char[64];
        Math.Abs(value).TryFormat(text, out int written, provider: System.Globalization.CultureInfo.InvariantCulture);
        int first = 0, last = written - 1;
        while (text[first] is '0' or '.') { first++; }
        while (text[last] is '0' or '.') { last--; }
        int digits = last - first + 1;
        for (int i = first; i <= last; i++) { if (text[i] == '.') { digits--; } }
        return 4 + digits;
    }

    private static int ZeroCount(string text) { int count = 0; foreach (char c in text) { if (c == 0) { count++; } } return count; }
    private static int ZeroCount(byte[] bytes) { int count = 0; foreach (byte b in bytes) { if (b == 0) { count++; } } return count; }

    internal static void Write<TMessages>(BinaryWriter writer, object? value, int depth, ref int count, DatabaseKeyWriter scalarWriter)
        where TMessages : struct, IGraphEncodingMessages
    {
        if (depth > MaxDepth || ++count > MaxValueCount)
        { throw new DatabaseTypeException(TMessages.CompositeLimit); }
        switch (value)
        {
            case Array array when array.Rank != 1 || array.GetLowerBound(0) != 0:
                throw new DatabaseTypeException(TMessages.CompositeRequired);
            case IReadOnlyDictionary<string, object?> map:
                if (map.Count > MaxValueCount - count) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
                writer.Write((byte)2); writer.Write(map.Count);
                foreach (var entry in map)
                {
                    int size = _utf8.GetByteCount(entry.Key);
                    if (size > MaxPayloadLength - writer.BaseStream.Length - 4) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
                    writer.Write(size); writer.Write(_utf8.GetBytes(entry.Key));
                    Write<TMessages>(writer, entry.Value, depth + 1, ref count, scalarWriter);
                }
                break;
            case IList list when value is not byte[]:
                if (list.Count > MaxValueCount - count) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
                writer.Write((byte)1); writer.Write(list.Count);
                foreach (var item in list) { Write<TMessages>(writer, item, depth + 1, ref count, scalarWriter); }
                break;
            case byte number: writer.Write((byte)3); writer.Write(number); break;
            case ushort number: writer.Write((byte)4); writer.Write(number); break;
            case uint number: writer.Write((byte)5); writer.Write(number); break;
            case ulong number: writer.Write((byte)6); writer.Write(number); break;
            case decimal number:
                writer.Write((byte)7);
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(number, bits);
                foreach (int part in bits) { writer.Write(part); }
                break;
            default:
                scalarWriter.Reset(); DatabaseValueCodec.Append(scalarWriter, value);
                writer.Write((byte)0); writer.Write(scalarWriter.Length); writer.Write(scalarWriter.WrittenSpan);
                break;
        }
        if (writer.BaseStream.Length > MaxPayloadLength) { throw new DatabaseTypeException(TMessages.CompositeLimit); }
    }

    private static object? Read<TMessages>(ref ReadOnlySpan<byte> payload, int depth, ref int count)
        where TMessages : struct, IGraphEncodingMessages
    {
        if (depth > MaxDepth || count > MaxValueCount || payload.IsEmpty)
        { throw new ProtocolException(TMessages.CompositeMalformed); }
        byte tag = payload[0]; payload = payload[1..];
        if (tag == 0)
        {
            int length = Length<TMessages>(ref payload);
            object? scalar = DatabaseValueCodec.DecodeComponent(payload[..length]);
            payload = payload[length..];
            return scalar;
        }
        if (tag is >= 3 and <= 6)
        {
            int size = 1 << (tag - 3);
            if (payload.Length < size) { throw new ProtocolException(TMessages.CompositeMalformed); }
            object number = tag switch { 3 => (object)payload[0], 4 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
                5 => BinaryPrimitives.ReadUInt32LittleEndian(payload), _ => BinaryPrimitives.ReadUInt64LittleEndian(payload) };
            payload = payload[size..]; return number;
        }
        if (tag == 7)
        {
            if (payload.Length < 16) { throw new ProtocolException(TMessages.CompositeMalformed); }
            int flags = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
            int scale = (flags >> 16) & 0xff;
            if ((flags & 0x7f00ffff) != 0 || scale > 28) { throw new ProtocolException(TMessages.CompositeMalformed); }
            decimal number = new(BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[8..]), flags < 0, (byte)scale);
            payload = payload[16..];
            return number;
        }
        if (tag is not (1 or 2) || payload.Length < 4) { throw new ProtocolException(TMessages.CompositeMalformed); }
        int items = BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
        // Check counts, minimum bytes and depth before any container allocation.
        if (items < 0 || items > MaxValueCount - count || items > payload.Length / (tag == 2 ? 5 : 1) ||
            items > 0 && depth == MaxDepth)
        { throw new ProtocolException(TMessages.CompositeMalformed); }
        count += items;
        if (tag == 1)
        {
            var list = new object?[items];
            for (int i = 0; i < items; i++) { list[i] = Read<TMessages>(ref payload, depth + 1, ref count); }
            return list;
        }
        var map = new Dictionary<string, object?>(items, StringComparer.Ordinal);
        for (int i = 0; i < items; i++)
        {
            int length = Length<TMessages>(ref payload);
            string key = _utf8.GetString(payload[..length]); payload = payload[length..];
            object? value = Read<TMessages>(ref payload, depth + 1, ref count);
            if (!map.TryAdd(key, value)) { throw new ProtocolException(TMessages.CompositeMalformed); }
        }
        return new ReadOnlyDictionary<string, object?>(map);
    }

    private static int Length<TMessages>(ref ReadOnlySpan<byte> payload)
        where TMessages : struct, IGraphEncodingMessages
    {
        if (payload.Length < 4) { throw new ProtocolException(TMessages.CompositeMalformed); }
        int size = BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
        if (size < 0 || size > payload.Length) { throw new ProtocolException(TMessages.CompositeMalformed); }
        return size;
    }
}
