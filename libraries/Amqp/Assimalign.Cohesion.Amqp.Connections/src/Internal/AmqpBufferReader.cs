using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

internal ref struct AmqpBufferReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _offset;

    public AmqpBufferReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    public int Remaining => _buffer.Length - _offset;

    public bool End => _offset >= _buffer.Length;

    public ReadOnlySpan<byte> UnreadSpan => _buffer[_offset..];

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_offset++];
    }

    public short ReadInt16()
    {
        EnsureAvailable(2);
        short value = BinaryPrimitives.ReadInt16BigEndian(_buffer[_offset..]);
        _offset += 2;
        return value;
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_buffer[_offset..]);
        _offset += 2;
        return value;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        int value = BinaryPrimitives.ReadInt32BigEndian(_buffer[_offset..]);
        _offset += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_offset..]);
        _offset += 4;
        return value;
    }

    public long ReadInt64()
    {
        EnsureAvailable(8);
        long value = BinaryPrimitives.ReadInt64BigEndian(_buffer[_offset..]);
        _offset += 8;
        return value;
    }

    public ulong ReadUInt64()
    {
        EnsureAvailable(8);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(_buffer[_offset..]);
        _offset += 8;
        return value;
    }

    public float ReadSingle()
    {
        return BitConverter.Int32BitsToSingle(ReadInt32());
    }

    public double ReadDouble()
    {
        return BitConverter.Int64BitsToDouble(ReadInt64());
    }

    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        EnsureAvailable(length);
        ReadOnlySpan<byte> slice = _buffer.Slice(_offset, length);
        _offset += length;
        return slice;
    }

    private void EnsureAvailable(int length)
    {
        if (Remaining < length)
        {
            throw new AmqpProtocolException("The AMQP payload ended unexpectedly.");
        }
    }
}
