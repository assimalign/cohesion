using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Protocol.Internal;

// The same traversal measures and writes the unchanged Path payload. Measuring checks the
// per-frame limit before any rental; encoding writes UTF-8 and scalar bits directly into spans.
internal ref struct GraphProtocolPathWriter
{
    private readonly Span<byte> _buffer;
    private readonly bool _measure;
    private int _position;

    internal GraphProtocolPathWriter(Span<byte> buffer, bool measure)
    {
        _buffer = buffer;
        _measure = measure;
        _position = 0;
    }

    internal readonly int Length => _position;

    private Span<byte> Take(int length)
    {
        if (length > ProtocolFrameHeader.MaxPayloadLength - _position)
        {
            throw new ProtocolException(ErrorMessages.PathPayloadTooLarge);
        }
        int start = _position;
        if (!_measure && length > _buffer.Length - start)
        {
            throw new ArgumentException(ErrorMessages.PathDestinationTooSmall, "destination");
        }
        _position += length;
        return _measure ? default : _buffer.Slice(start, length);
    }

    internal void WriteByte(byte value)
    {
        Span<byte> target = Take(1);
        if (!_measure) { target[0] = value; }
    }

    internal void WriteInt32(int value)
    {
        Span<byte> target = Take(sizeof(int));
        if (!_measure) { BinaryPrimitives.WriteInt32BigEndian(target, value); }
    }

    internal void WriteInt64(long value)
    {
        Span<byte> target = Take(sizeof(long));
        if (!_measure) { BinaryPrimitives.WriteInt64BigEndian(target, value); }
    }

    internal void WriteUInt16(ushort value)
    {
        Span<byte> target = Take(sizeof(ushort));
        if (!_measure) { BinaryPrimitives.WriteUInt16BigEndian(target, value); }
    }

    internal void WriteString(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        WriteInt32(length);
        Span<byte> target = Take(length);
        if (!_measure) { Encoding.UTF8.GetBytes(value, target); }
    }

    internal void WriteProperties(IReadOnlyDictionary<string, object?> properties)
    {
        WriteInt32(properties.Count);
        Span<int> parts = stackalloc int[4];
        foreach (var (name, value) in properties)
        {
            WriteString(name);
            switch (value)
            {
                case null: WriteByte(0); break;
                case false: WriteByte(1); break;
                case true: WriteByte(2); break;
                case string text: WriteByte(3); WriteString(text); break;
                case byte number: WriteByte(4); WriteByte(number); break;
                case sbyte number: WriteByte(5); WriteByte(unchecked((byte)number)); break;
                case short number: WriteByte(6); WriteUInt16(unchecked((ushort)number)); break;
                case ushort number: WriteByte(7); WriteUInt16(number); break;
                case int number: WriteByte(8); WriteInt32(number); break;
                case uint number: WriteByte(9); WriteInt32(unchecked((int)number)); break;
                case long number: WriteByte(10); WriteInt64(number); break;
                case ulong number: WriteByte(11); WriteInt64(unchecked((long)number)); break;
                case float number when float.IsFinite(number):
                    WriteByte(12); WriteInt32(BitConverter.SingleToInt32Bits(number)); break;
                case double number when double.IsFinite(number):
                    WriteByte(13); WriteInt64(BitConverter.DoubleToInt64Bits(number)); break;
                case decimal number:
                    WriteByte(14);
                    decimal.GetBits(number, parts);
                    foreach (int part in parts) { WriteInt32(part); }
                    break;
                default: throw new ProtocolException(ErrorMessages.PathPropertyUnsupported);
            }
        }
    }
}
