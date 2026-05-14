using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Forward-only big-endian writer over a DNS wire-format buffer. Mirrors
/// <see cref="DnsWireReader"/>: centralises byte-order conversion and bounds checking so the
/// serializers stay readable.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DnsWireWriter"/> is a <see langword="ref struct"/> so it can wrap a stack-only
/// <see cref="Span{Byte}"/> without allocation. Insufficient buffer is signalled with
/// <see cref="DnsException"/> &#8211; the writer never grows the buffer, leaving allocation
/// strategy to the caller (typically a rented pool buffer sized to RFC 6891 EDNS payload
/// limits).
/// </para>
/// <para>
/// The writer also maintains a name-compression table (<see cref="LabelOffsets"/>) used by
/// the name encoder to deduplicate suffixes per RFC 1035 &#167; 4.1.4.
/// </para>
/// </remarks>
internal ref struct DnsWireWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public DnsWireWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Current zero-based offset into the buffer.</summary>
    public int Position => _position;

    /// <summary>Total length of the underlying buffer.</summary>
    public int Length => _buffer.Length;

    /// <summary>Octets remaining between the cursor and the end of the buffer.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>
    /// Per-message name-compression table. Maps the dotted lowercase suffix to its first
    /// occurrence offset. Reset between messages by the caller (writers are short-lived so
    /// this is rarely an issue).
    /// </summary>
    public Dictionary<string, int> LabelOffsets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Writes one octet.
    /// </summary>
    public void WriteUInt8(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>
    /// Writes a 16-bit unsigned integer in big-endian.
    /// </summary>
    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(_position, 2), value);
        _position += 2;
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer in big-endian.
    /// </summary>
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> directly to the buffer. The parameter is
    /// <see langword="scoped"/> so callers can pass <c>stackalloc</c>-backed spans without
    /// triggering escape-analysis errors on this <see langword="ref struct"/>.
    /// </summary>
    public void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.Slice(_position, bytes.Length));
        _position += bytes.Length;
    }

    /// <summary>
    /// Reserves <paramref name="length"/> octets at the current cursor and returns a mutable
    /// span into them. The cursor advances past the reservation. Used by record writers to
    /// patch a 16-bit RDLENGTH after writing the RDATA itself.
    /// </summary>
    public Span<byte> Reserve(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be non-negative.");
        }
        EnsureCapacity(length);
        Span<byte> slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>
    /// Patches a 16-bit big-endian unsigned integer at <paramref name="offset"/> without
    /// moving the cursor.
    /// </summary>
    public void PatchUInt16(int offset, ushort value)
    {
        if (offset < 0 || offset + 2 > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.Slice(offset, 2), value);
    }

    /// <summary>
    /// Returns the slice of the buffer written so far.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.Slice(0, _position);

    /// <summary>
    /// Direct, mutable access to the underlying buffer. Used by the name encoder when
    /// emitting raw label bytes.
    /// </summary>
    public Span<byte> Buffer => _buffer;

    private void EnsureCapacity(int count)
    {
        if (_buffer.Length - _position < count)
        {
            DnsException.ThrowMalformed(
                $"wire buffer overrun: tried to write {count} octets at offset {_position} but only {_buffer.Length - _position} remain");
        }
    }
}
