using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

internal static class GraphProtocolProperties
{
    internal static void Write(List<byte> buffer, IReadOnlyDictionary<string, object?> properties)
    {
        ProtocolPayload.WriteInt32(buffer, properties.Count);
        foreach (var (name, value) in properties)
        {
            ProtocolPayload.WriteString(buffer, name);
            switch (value)
            {
                case null: buffer.Add(0); break;
                case false: buffer.Add(1); break;
                case true: buffer.Add(2); break;
                case string text: buffer.Add(3); ProtocolPayload.WriteString(buffer, text); break;
                case byte number: buffer.Add(4); buffer.Add(number); break;
                case sbyte number: buffer.Add(5); buffer.Add(unchecked((byte)number)); break;
                case short number: buffer.Add(6); ProtocolPayload.WriteUInt16(buffer, unchecked((ushort)number)); break;
                case ushort number: buffer.Add(7); ProtocolPayload.WriteUInt16(buffer, number); break;
                case int number: buffer.Add(8); ProtocolPayload.WriteInt32(buffer, number); break;
                case uint number: buffer.Add(9); ProtocolPayload.WriteInt32(buffer, unchecked((int)number)); break;
                case long number: buffer.Add(10); ProtocolPayload.WriteInt64(buffer, number); break;
                case ulong number: buffer.Add(11); ProtocolPayload.WriteInt64(buffer, unchecked((long)number)); break;
                case float number when float.IsFinite(number):
                    buffer.Add(12); ProtocolPayload.WriteInt32(buffer, BitConverter.SingleToInt32Bits(number)); break;
                case double number when double.IsFinite(number):
                    buffer.Add(13); ProtocolPayload.WriteInt64(buffer, BitConverter.DoubleToInt64Bits(number)); break;
                case decimal number:
                    buffer.Add(14);
                    foreach (int part in decimal.GetBits(number)) { ProtocolPayload.WriteInt32(buffer, part); }
                    break;
                default: throw new ProtocolException("Unsupported graph property type or non-finite number.");
            }
        }
    }

    internal static IReadOnlyDictionary<string, object?> Read(ReadOnlySpan<byte> payload, ref int position)
    {
        int count = GraphProtocolPathMessage.ReadCount(payload, ref position, 5);
        var properties = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string name = ProtocolPayload.ReadString(payload, ref position);
            byte tag = ReadByte(payload, ref position);
            object? value = tag switch
            {
                0 => null,
                1 => false,
                2 => true,
                3 => ProtocolPayload.ReadString(payload, ref position),
                4 => ReadByte(payload, ref position),
                5 => unchecked((sbyte)ReadByte(payload, ref position)),
                6 => unchecked((short)ProtocolPayload.ReadUInt16(payload, ref position)),
                7 => ProtocolPayload.ReadUInt16(payload, ref position),
                8 => ProtocolPayload.ReadInt32(payload, ref position),
                9 => unchecked((uint)ProtocolPayload.ReadInt32(payload, ref position)),
                10 => ProtocolPayload.ReadInt64(payload, ref position),
                11 => unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)),
                12 => BitConverter.Int32BitsToSingle(ProtocolPayload.ReadInt32(payload, ref position)),
                13 => BitConverter.Int64BitsToDouble(ProtocolPayload.ReadInt64(payload, ref position)),
                14 => ReadDecimal(payload, ref position),
                _ => throw new ProtocolException("Unknown graph property type tag."),
            };
            if (value is float single && !float.IsFinite(single) || value is double number && !double.IsFinite(number))
            {
                throw new ProtocolException("Graph properties require finite numbers.");
            }
            if (!properties.TryAdd(name, value)) { throw new ProtocolException("Duplicate graph property name."); }
        }
        return properties;
    }

    private static byte ReadByte(ReadOnlySpan<byte> payload, ref int position)
    {
        if (position >= payload.Length) { throw new ProtocolException("Truncated graph property payload."); }
        return payload[position++];
    }

    private static decimal ReadDecimal(ReadOnlySpan<byte> payload, ref int position)
    {
        int low = ProtocolPayload.ReadInt32(payload, ref position);
        int middle = ProtocolPayload.ReadInt32(payload, ref position);
        int high = ProtocolPayload.ReadInt32(payload, ref position);
        int flags = ProtocolPayload.ReadInt32(payload, ref position);
        if ((flags & 0x7f00ffff) != 0 || ((flags >> 16) & 0xff) > 28)
        {
            throw new ProtocolException("Malformed graph decimal flags.");
        }
        return new decimal(low, middle, high, flags < 0, (byte)((flags >> 16) & 0xff));
    }
}
