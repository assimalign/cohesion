using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>Writes binary values directly to a caller-owned buffer writer without internal buffering.</summary>
/// <remarks>Each nonempty write requests its exact byte count and advances by that count. Empty writes do not touch the output.
/// The writer never owns or disposes the output. UTF-8 encoding uses replacement fallback.</remarks>
// Deviates from the repo interface-first rule per the Stage 1 brief: this is the stack-only counterpart of BinaryContentReader.
public ref struct BinaryContentWriter
{
    private readonly IBufferWriter<byte> _output;
    private long _bytesWritten;

    /// <summary>Creates a writer over an existing output.</summary>
    /// <param name="output">The caller-owned output buffer writer.</param>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    public BinaryContentWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _bytesWritten = 0;
    }

    /// <summary>Gets the number of bytes advanced on the output.</summary>
    public readonly long BytesWritten => _bytesWritten;

    /// <summary>Writes Byte in 1 byte.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteByte(byte value)
    {
        _output.GetSpan(1)[0] = value;
        Advance(1);
    }

    /// <summary>Writes SByte in 1 byte.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteSByte(sbyte value)
    {
        WriteByte(unchecked((byte)value));
    }

    /// <summary>Writes UInt16BigEndian in 2 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt16BigEndian(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_output.GetSpan(2), value);
        Advance(2);
    }

    /// <summary>Writes Int16BigEndian in 2 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt16BigEndian(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_output.GetSpan(2), value);
        Advance(2);
    }

    /// <summary>Writes UInt32BigEndian in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt32BigEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_output.GetSpan(4), value);
        Advance(4);
    }

    /// <summary>Writes Int32BigEndian in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt32BigEndian(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_output.GetSpan(4), value);
        Advance(4);
    }

    /// <summary>Writes UInt64BigEndian in 8 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt64BigEndian(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_output.GetSpan(8), value);
        Advance(8);
    }

    /// <summary>Writes Int64BigEndian in 8 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt64BigEndian(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_output.GetSpan(8), value);
        Advance(8);
    }

    /// <summary>Writes UInt16LittleEndian in 2 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt16LittleEndian(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_output.GetSpan(2), value);
        Advance(2);
    }

    /// <summary>Writes Int16LittleEndian in 2 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt16LittleEndian(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_output.GetSpan(2), value);
        Advance(2);
    }

    /// <summary>Writes UInt32LittleEndian in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt32LittleEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_output.GetSpan(4), value);
        Advance(4);
    }

    /// <summary>Writes Int32LittleEndian in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt32LittleEndian(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_output.GetSpan(4), value);
        Advance(4);
    }

    /// <summary>Writes UInt64LittleEndian in 8 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt64LittleEndian(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_output.GetSpan(8), value);
        Advance(8);
    }

    /// <summary>Writes Int64LittleEndian in 8 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt64LittleEndian(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_output.GetSpan(8), value);
        Advance(8);
    }

    /// <summary>Writes UInt24BigEndian in 3 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit in 24 bits.</exception>
    public void WriteUInt24BigEndian(uint value)
    {
        WriteUIntBigEndian(value, 3);
    }

    /// <summary>Writes SingleBigEndian in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteSingleBigEndian(float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_output.GetSpan(4), value);
        Advance(4);
    }

    /// <summary>Writes DoubleBigEndian in 8 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteDoubleBigEndian(double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(_output.GetSpan(8), value);
        Advance(8);
    }

    /// <summary>Writes FourCC in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteFourCC(FourCC value)
    {
        WriteUInt32BigEndian(value.Value);
    }

    /// <summary>Writes FixedPoint16x16 in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteFixedPoint16x16(FixedPoint16x16 value)
    {
        WriteInt32BigEndian(value.RawValue);
    }

    /// <summary>Writes UnsignedFixedPoint16x16 in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUnsignedFixedPoint16x16(UnsignedFixedPoint16x16 value)
    {
        WriteUInt32BigEndian(value.RawValue);
    }

    /// <summary>Writes FixedPoint8x8 in 2 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteFixedPoint8x8(FixedPoint8x8 value)
    {
        WriteInt16BigEndian(value.RawValue);
    }

    /// <summary>Writes FixedPoint2x30 in 4 bytes.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteFixedPoint2x30(FixedPoint2x30 value)
    {
        WriteInt32BigEndian(value.RawValue);
    }

    /// <summary>Writes an unsigned big-endian integer of zero to eight bytes.</summary>
    /// <param name="value">The value to encode; only zero fits a zero-byte field.</param>
    /// <param name="byteCount">The encoded width, from zero to eight.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is invalid or the value does not fit.</exception>
    public void WriteUIntBigEndian(ulong value, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, 8);
        if (byteCount < 8 && value >= (1UL << (byteCount * 8)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), ErrorMessages.IntegerDoesNotFit);
        }
        if (byteCount == 0)
        {
            return;
        }
        var bytes = _output.GetSpan(byteCount);
        for (var i = byteCount - 1; i >= 0; i--)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }
        Advance(byteCount);
    }

    /// <summary>Writes an unsigned little-endian integer of zero to eight bytes.</summary>
    /// <param name="value">The value to encode; only zero fits a zero-byte field.</param>
    /// <param name="byteCount">The encoded width, from zero to eight.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is invalid or the value does not fit.</exception>
    public void WriteUIntLittleEndian(ulong value, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, 8);
        if (byteCount < 8 && value >= (1UL << (byteCount * 8)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), ErrorMessages.IntegerDoesNotFit);
        }
        if (byteCount == 0)
        {
            return;
        }
        var bytes = _output.GetSpan(byteCount);
        for (var i = 0; i < byteCount; i++)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }
        Advance(byteCount);
    }

    /// <summary>Writes the supplied bytes.</summary>
    /// <param name="value">The bytes to copy.</param>
    public void WriteBytes(scoped ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }
        value.CopyTo(_output.GetSpan(value.Length));
        Advance(value.Length);
    }

    /// <summary>Writes exactly the requested number of zero bytes.</summary>
    /// <param name="count">The nonnegative byte count.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public void WriteZeros(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }
        _output.GetSpan(count)[..count].Clear();
        Advance(count);
    }

    /// <summary>Writes a string as UTF-8 without a terminator.</summary>
    /// <param name="value">The text to encode.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public void WriteUtf8String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = Encoding.UTF8.GetByteCount(value);
        if (count == 0)
        {
            return;
        }
        Encoding.UTF8.GetBytes(value, _output.GetSpan(count));
        Advance(count);
    }

    /// <summary>Writes a string as UTF-8 followed by one NUL byte in a single output request.</summary>
    /// <param name="value">The text to encode.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The encoded string leaves no room for its terminator.</exception>
    public void WriteNullTerminatedUtf8String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = Encoding.UTF8.GetByteCount(value);
        ArgumentOutOfRangeException.ThrowIfEqual(count, int.MaxValue);
        var bytes = _output.GetSpan(count + 1);
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[count] = 0;
        Advance(count + 1);
    }

    private void Advance(int count)
    {
        _output.Advance(count);
        _bytesWritten += count;
    }
}
