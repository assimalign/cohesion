using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Forward-only big-endian reader over a DNS wire-format buffer. The DNS protocol uses
/// network byte order (big-endian) for every multi-octet field; this wrapper centralizes the
/// byte-order conversion and bounds-checking so the parsers can stay readable.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DnsWireReader"/> is a <see langword="ref struct"/> so it can wrap a stack-only
/// <see cref="ReadOnlySpan{Byte}"/> without allocation. Each <c>Read*</c> call advances
/// <see cref="Position"/>; on insufficient buffer the reader throws
/// <see cref="DnsException"/> with <see cref="DnsErrorCode.Malformed"/> so caller code does
/// not need to repeat the check.
/// </para>
/// <para>
/// The reader does not own the buffer &#8211; it never mutates it &#8211; so several readers
/// may view the same message in parallel (used by the name decoder when following
/// compression pointers).
/// </para>
/// </remarks>
internal ref struct DnsWireReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public DnsWireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public DnsWireReader(ReadOnlySpan<byte> buffer, int position)
    {
        _buffer = buffer;
        _position = position;
    }

    /// <summary>Current zero-based offset into the buffer.</summary>
    public int Position => _position;

    /// <summary>Total length of the underlying buffer.</summary>
    public int Length => _buffer.Length;

    /// <summary>Octets remaining between the cursor and the end of the buffer.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>Raw view of the buffer; never mutated.</summary>
    public ReadOnlySpan<byte> Buffer => _buffer;

    /// <summary>
    /// Reads one octet and advances the cursor.
    /// </summary>
    public byte ReadUInt8()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    /// <summary>
    /// Reads a big-endian 16-bit unsigned integer and advances the cursor.
    /// </summary>
    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return value;
    }

    /// <summary>
    /// Reads a big-endian 32-bit unsigned integer and advances the cursor.
    /// </summary>
    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>
    /// Returns a sub-span of <paramref name="length"/> octets starting at the current cursor
    /// and advances the cursor.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be non-negative.");
        }
        EnsureAvailable(length);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>
    /// Peeks the next octet without advancing the cursor.
    /// </summary>
    public byte PeekUInt8()
    {
        EnsureAvailable(1);
        return _buffer[_position];
    }

    /// <summary>
    /// Peeks the next big-endian 16-bit unsigned integer without advancing the cursor.
    /// </summary>
    public ushort PeekUInt16()
    {
        EnsureAvailable(2);
        return BinaryPrimitives.ReadUInt16BigEndian(_buffer.Slice(_position, 2));
    }

    /// <summary>
    /// Repositions the cursor to <paramref name="offset"/>. Used by the name decoder when
    /// following compression pointers.
    /// </summary>
    /// <exception cref="DnsException">Offset falls outside the buffer.</exception>
    public void Seek(int offset)
    {
        if ((uint)offset > (uint)_buffer.Length)
        {
            DnsException.ThrowMalformed($"wire offset {offset} is outside the buffer (length {_buffer.Length})");
        }
        _position = offset;
    }

    /// <summary>
    /// Throws <see cref="DnsException"/> with <see cref="DnsErrorCode.Malformed"/> when fewer
    /// than <paramref name="count"/> octets remain.
    /// </summary>
    private void EnsureAvailable(int count)
    {
        if (_buffer.Length - _position < count)
        {
            DnsException.ThrowMalformed(
                $"wire buffer underrun: needed {count} octets at offset {_position} but only {_buffer.Length - _position} remain");
        }
    }
}
