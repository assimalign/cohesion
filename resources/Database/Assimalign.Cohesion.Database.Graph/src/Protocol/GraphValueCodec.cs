using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Encodes Graph composite values around scalar DatabaseValueCodec components.</summary>
/// <remarks>This format requires GraphProtocol.CompositeValues. It is never an index key format.
/// Lists decode to object arrays; maps decode to read-only ordinal string dictionaries.</remarks>
public static class GraphValueCodec
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
    public static byte[] Encode(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, _utf8, leaveOpen: true);
        int count = 0;
        try { Write(writer, value, 0, ref count); }
        catch (EncoderFallbackException error)
        { throw new DatabaseTypeException(ErrorMessages.CompositeMalformed, error); }
        return stream.ToArray();
    }

    /// <summary>Decodes one bounded composite component.</summary>
    /// <param name="payload">The complete component.</param>
    /// <returns>The decoded value with scalar runtime types preserved.</returns>
    /// <exception cref="ProtocolException">The component is malformed or exceeds a bound.</exception>
    public static object? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        int count = 0;
        var remaining = payload;
        try
        {
            object? value = Read(ref remaining, 0, ref count);
            if (!remaining.IsEmpty) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
            return value;
        }
        catch (Exception error) when (error is DatabaseTypeException or ArgumentException or OverflowException)
        { throw new ProtocolException(ErrorMessages.CompositeMalformed, error); }
    }

    private static void Write(BinaryWriter writer, object? value, int depth, ref int count)
    {
        if (depth > MaxDepth || ++count > MaxValueCount)
        { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
        switch (value)
        {
            case Array array when array.Rank != 1 || array.GetLowerBound(0) != 0:
                throw new DatabaseTypeException(ErrorMessages.CompositeRequired);
            case IReadOnlyDictionary<string, object?> map:
                if (map.Count > MaxValueCount - count) { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
                writer.Write((byte)2); writer.Write(map.Count);
                foreach (var entry in map)
                {
                    int size = _utf8.GetByteCount(entry.Key);
                    if (size > MaxPayloadLength - writer.BaseStream.Length - 4) { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
                    writer.Write(size); writer.Write(_utf8.GetBytes(entry.Key));
                    Write(writer, entry.Value, depth + 1, ref count);
                }
                break;
            case IList list when value is not byte[]:
                if (list.Count > MaxValueCount - count) { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
                writer.Write((byte)1); writer.Write(list.Count);
                foreach (var item in list) { Write(writer, item, depth + 1, ref count); }
                break;
            case byte number: writer.Write((byte)3); writer.Write(number); break;
            case ushort number: writer.Write((byte)4); writer.Write(number); break;
            case uint number: writer.Write((byte)5); writer.Write(number); break;
            case ulong number: writer.Write((byte)6); writer.Write(number); break;
            default:
                if (value is string text && (long)text.Length * 2 > MaxPayloadLength - writer.BaseStream.Length ||
                    value is byte[] bytes && bytes.Length > MaxPayloadLength - writer.BaseStream.Length)
                { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
                byte[] scalar = DatabaseValueCodec.EncodeComponent(value);
                writer.Write((byte)0); writer.Write(scalar.Length); writer.Write(scalar);
                break;
        }
        if (writer.BaseStream.Length > MaxPayloadLength) { throw new DatabaseTypeException(ErrorMessages.CompositeLimit); }
    }

    private static object? Read(ref ReadOnlySpan<byte> payload, int depth, ref int count)
    {
        if (depth > MaxDepth || ++count > MaxValueCount || payload.IsEmpty)
        { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        byte tag = payload[0]; payload = payload[1..];
        if (tag == 0)
        {
            int length = Length(ref payload);
            object? scalar = DatabaseValueCodec.DecodeComponent(payload[..length]);
            payload = payload[length..];
            return scalar;
        }
        if (tag is >= 3 and <= 6)
        {
            int size = 1 << (tag - 3);
            if (payload.Length < size) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
            object number = tag switch { 3 => (object)payload[0], 4 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
                5 => BinaryPrimitives.ReadUInt32LittleEndian(payload), _ => BinaryPrimitives.ReadUInt64LittleEndian(payload) };
            payload = payload[size..]; return number;
        }
        if (tag is not (1 or 2) || payload.Length < 4) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        int items = BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
        // Check counts, minimum bytes and depth before any container allocation.
        if (items < 0 || items > MaxValueCount - count || items > payload.Length / (tag == 2 ? 5 : 1) ||
            items > 0 && depth == MaxDepth)
        { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        if (tag == 1)
        {
            var list = new object?[items];
            for (int i = 0; i < items; i++) { list[i] = Read(ref payload, depth + 1, ref count); }
            return list;
        }
        var map = new Dictionary<string, object?>(items, StringComparer.Ordinal);
        for (int i = 0; i < items; i++)
        {
            int length = Length(ref payload);
            string key = _utf8.GetString(payload[..length]); payload = payload[length..];
            object? value = Read(ref payload, depth + 1, ref count);
            if (!map.TryAdd(key, value)) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        }
        return new ReadOnlyDictionary<string, object?>(map);
    }

    private static int Length(ref ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        int size = BinaryPrimitives.ReadInt32LittleEndian(payload); payload = payload[4..];
        if (size < 0 || size > payload.Length) { throw new ProtocolException(ErrorMessages.CompositeMalformed); }
        return size;
    }
}
