using System;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A forward-only, allocation-free reader over a buffered binary payload.</summary>
/// <remarks>Only string reads allocate. Failed reads leave the cursor unchanged and report the absolute start position.
/// UTF-8 decoding replaces invalid sequences with the Unicode replacement character; format-specific validation belongs to callers.</remarks>
// Deviates from the repo interface-first rule per the Stage 1 brief: a ref struct preserves span lifetimes.
public ref struct BinaryContentReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly long _baseOffset;
    private int _consumed;

    /// <summary>Creates a reader at the start of a buffered payload.</summary>
    /// <param name="buffer">The payload, borrowed for the reader's lifetime.</param>
    /// <param name="baseOffset">The nonnegative absolute position of the first byte.</param>
    /// <exception cref="ArgumentOutOfRangeException">The base offset is negative or the buffer's end cannot be represented by Int64.</exception>
    public BinaryContentReader(ReadOnlySpan<byte> buffer, long baseOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseOffset, long.MaxValue - buffer.Length);
        _buffer = buffer;
        _baseOffset = baseOffset;
        _consumed = 0;
    }

    /// <summary>Gets the absolute position of the next byte.</summary>
    public readonly long Position => _baseOffset + _consumed;

    /// <summary>Gets the number of bytes consumed.</summary>
    public readonly int Consumed => _consumed;

    /// <summary>Gets the number of bytes remaining.</summary>
    public readonly int Remaining => _buffer.Length - _consumed;

    /// <summary>Gets whether the buffer has been consumed completely.</summary>
    public readonly bool IsEnd => Remaining == 0;

    /// <summary>Gets a borrowed span over the unconsumed bytes.</summary>
    public readonly ReadOnlySpan<byte> UnreadSpan => _buffer[_consumed..];

    /// <summary>Reads 1 byte as Byte.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 1 bytes remain; the code is Truncated.</exception>
    public byte ReadByte() => ReadBytes(1)[0];

    /// <summary>Reads 1 byte as SByte.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 1 bytes remain; the code is Truncated.</exception>
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    /// <summary>Reads 2 bytes as UInt16BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 2 bytes remain; the code is Truncated.</exception>
    public ushort ReadUInt16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));

    /// <summary>Reads 2 bytes as Int16BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 2 bytes remain; the code is Truncated.</exception>
    public short ReadInt16BigEndian() => BinaryPrimitives.ReadInt16BigEndian(ReadBytes(2));

    /// <summary>Reads 4 bytes as UInt32BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public uint ReadUInt32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    /// <summary>Reads 4 bytes as Int32BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public int ReadInt32BigEndian() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));

    /// <summary>Reads 8 bytes as UInt64BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 8 bytes remain; the code is Truncated.</exception>
    public ulong ReadUInt64BigEndian() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));

    /// <summary>Reads 8 bytes as Int64BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 8 bytes remain; the code is Truncated.</exception>
    public long ReadInt64BigEndian() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

    /// <summary>Reads 2 bytes as UInt16LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 2 bytes remain; the code is Truncated.</exception>
    public ushort ReadUInt16LittleEndian() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    /// <summary>Reads 2 bytes as Int16LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 2 bytes remain; the code is Truncated.</exception>
    public short ReadInt16LittleEndian() => BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(2));

    /// <summary>Reads 4 bytes as UInt32LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public uint ReadUInt32LittleEndian() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    /// <summary>Reads 4 bytes as Int32LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public int ReadInt32LittleEndian() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));

    /// <summary>Reads 8 bytes as UInt64LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 8 bytes remain; the code is Truncated.</exception>
    public ulong ReadUInt64LittleEndian() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    /// <summary>Reads 8 bytes as Int64LittleEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 8 bytes remain; the code is Truncated.</exception>
    public long ReadInt64LittleEndian() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));

    /// <summary>Reads 3 bytes as UInt24BigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 3 bytes remain; the code is Truncated.</exception>
    public uint ReadUInt24BigEndian() => (uint)ReadUIntBigEndian(3);

    /// <summary>Reads 4 bytes as SingleBigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public float ReadSingleBigEndian() => BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4));

    /// <summary>Reads 8 bytes as DoubleBigEndian.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 8 bytes remain; the code is Truncated.</exception>
    public double ReadDoubleBigEndian() => BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8));

    /// <summary>Reads 4 bytes as FourCC.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public FourCC ReadFourCC() => FourCC.FromUInt32(ReadUInt32BigEndian());

    /// <summary>Reads 4 bytes as FixedPoint16x16.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public FixedPoint16x16 ReadFixedPoint16x16() => FixedPoint16x16.FromRaw(ReadInt32BigEndian());

    /// <summary>Reads 4 bytes as UnsignedFixedPoint16x16.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public UnsignedFixedPoint16x16 ReadUnsignedFixedPoint16x16() => UnsignedFixedPoint16x16.FromRaw(ReadUInt32BigEndian());

    /// <summary>Reads 2 bytes as FixedPoint8x8.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 2 bytes remain; the code is Truncated.</exception>
    public FixedPoint8x8 ReadFixedPoint8x8() => FixedPoint8x8.FromRaw(ReadInt16BigEndian());

    /// <summary>Reads 4 bytes as FixedPoint2x30.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ContentFormatException">Fewer than 4 bytes remain; the code is Truncated.</exception>
    public FixedPoint2x30 ReadFixedPoint2x30() => FixedPoint2x30.FromRaw(ReadInt32BigEndian());

    /// <summary>Reads an unsigned big-endian integer of zero to eight bytes.</summary>
    /// <param name="byteCount">The encoded width, from zero to eight. Zero returns zero.</param>
    /// <returns>The unsigned value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width is outside zero to eight.</exception>
    /// <exception cref="ContentFormatException">The input is truncated at the start of the read.</exception>
    public ulong ReadUIntBigEndian(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, 8);
        var bytes = ReadBytes(byteCount);
        ulong value = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }
        return value;
    }

    /// <summary>Reads an unsigned little-endian integer of zero to eight bytes.</summary>
    /// <param name="byteCount">The encoded width, from zero to eight. Zero returns zero.</param>
    /// <returns>The unsigned value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width is outside zero to eight.</exception>
    /// <exception cref="ContentFormatException">The input is truncated at the start of the read.</exception>
    public ulong ReadUIntLittleEndian(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, 8);
        var bytes = ReadBytes(byteCount);
        ulong value = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        return value;
    }

    /// <summary>Reads a borrowed span of exactly the requested length.</summary>
    /// <param name="count">The nonnegative byte count.</param>
    /// <returns>A slice of the original buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    /// <exception cref="ContentFormatException">The input is truncated at the start of the read.</exception>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Remaining)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, Position);
        }
        var result = _buffer.Slice(_consumed, count);
        _consumed += count;
        return result;
    }

    /// <summary>Fills a destination from the buffer.</summary>
    /// <param name="destination">The destination to fill.</param>
    /// <exception cref="ContentFormatException">The input is truncated; neither the destination nor the cursor changes.</exception>
    public void ReadExactly(scoped Span<byte> destination) => ReadBytes(destination.Length).CopyTo(destination);

    /// <summary>Advances by exactly the requested count.</summary>
    /// <param name="count">The nonnegative byte count.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    /// <exception cref="ContentFormatException">The input is truncated at the start of the skip.</exception>
    public void Skip(int count) => ReadBytes(count);

    /// <summary>Decodes exactly the requested bytes as UTF-8.</summary>
    /// <param name="byteCount">The nonnegative encoded byte count.</param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    /// <exception cref="ContentFormatException">The input is truncated at the start of the read.</exception>
    public string ReadUtf8String(int byteCount) => Encoding.UTF8.GetString(ReadBytes(byteCount));

    /// <summary>Reads UTF-8 through the first NUL, consuming that terminator, or through the buffer end.</summary>
    /// <returns>The decoded string without the terminator.</returns>
    /// <remarks>A missing NUL is accepted: BMFF strings at the end of a payload may omit it.</remarks>
    public string ReadNullTerminatedUtf8String()
    {
        var index = UnreadSpan.IndexOf((byte)0);
        var result = ReadUtf8String(index < 0 ? Remaining : index);
        if (index >= 0)
        {
            _consumed++;
        }
        return result;
    }

    /// <summary>Tries to read Byte, leaving the cursor unchanged on insufficient input.</summary>
    /// <param name="value">The value read, or zero on insufficient input.</param>
    /// <returns>Whether 1 bytes were available.</returns>
    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1)
        {
            value = default;
            return false;
        }
        value = ReadByte();
        return true;
    }

    /// <summary>Tries to read UInt16BigEndian, leaving the cursor unchanged on insufficient input.</summary>
    /// <param name="value">The value read, or zero on insufficient input.</param>
    /// <returns>Whether 2 bytes were available.</returns>
    public bool TryReadUInt16BigEndian(out ushort value)
    {
        if (Remaining < 2)
        {
            value = default;
            return false;
        }
        value = ReadUInt16BigEndian();
        return true;
    }

    /// <summary>Tries to read UInt32BigEndian, leaving the cursor unchanged on insufficient input.</summary>
    /// <param name="value">The value read, or zero on insufficient input.</param>
    /// <returns>Whether 4 bytes were available.</returns>
    public bool TryReadUInt32BigEndian(out uint value)
    {
        if (Remaining < 4)
        {
            value = default;
            return false;
        }
        value = ReadUInt32BigEndian();
        return true;
    }

    /// <summary>Tries to read UInt64BigEndian, leaving the cursor unchanged on insufficient input.</summary>
    /// <param name="value">The value read, or zero on insufficient input.</param>
    /// <returns>Whether 8 bytes were available.</returns>
    public bool TryReadUInt64BigEndian(out ulong value)
    {
        if (Remaining < 8)
        {
            value = default;
            return false;
        }
        value = ReadUInt64BigEndian();
        return true;
    }

    /// <summary>Tries to read a borrowed span, leaving the cursor unchanged on insufficient input.</summary>
    /// <param name="count">The nonnegative byte count.</param>
    /// <param name="value">The bytes read, or an empty span on insufficient input.</param>
    /// <returns>Whether the requested bytes were available.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Remaining < count)
        {
            value = default;
            return false;
        }
        value = ReadBytes(count);
        return true;
    }
}
