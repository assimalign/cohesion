using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// An order-preserving, byte-comparable index key.
/// </summary>
/// <remarks>
/// Keys compare by unsigned lexicographic byte order, so any component encoding
/// that preserves its type's natural order under that comparison can participate
/// in a key (integers are encoded big-endian with the sign bit flipped; string
/// collation encoding is provided by the shared type system). Composite keys
/// concatenate component encodings.
/// </remarks>
public readonly struct IndexKey : IEquatable<IndexKey>, IComparable<IndexKey>
{
    private readonly ReadOnlyMemory<byte> _encoded;

    /// <summary>
    /// Initializes a new <see cref="IndexKey"/> over an already order-preserving encoding.
    /// </summary>
    /// <param name="encoded">The encoded key bytes.</param>
    public IndexKey(ReadOnlyMemory<byte> encoded)
    {
        _encoded = encoded;
    }

    /// <summary>
    /// Gets the encoded key bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Encoded => _encoded;

    /// <summary>
    /// Gets the length of the encoded key in bytes.
    /// </summary>
    public int Length => _encoded.Length;

    /// <summary>
    /// Encodes a signed 64-bit integer as an order-preserving key component.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>A key whose byte order matches the numeric order of the input.</returns>
    public static IndexKey FromInt64(long value)
    {
        var buffer = new byte[sizeof(ulong)];
        // Flipping the sign bit maps the signed range onto the unsigned range
        // order-preservingly; big-endian makes byte order match numeric order.
        BinaryPrimitives.WriteUInt64BigEndian(buffer, (ulong)value ^ 0x8000_0000_0000_0000UL);
        return new IndexKey(buffer);
    }

    /// <summary>
    /// Encodes an unsigned 64-bit integer as an order-preserving key component.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>A key whose byte order matches the numeric order of the input.</returns>
    public static IndexKey FromUInt64(ulong value)
    {
        var buffer = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        return new IndexKey(buffer);
    }

    /// <inheritdoc />
    public int CompareTo(IndexKey other) => _encoded.Span.SequenceCompareTo(other._encoded.Span);

    /// <inheritdoc />
    public bool Equals(IndexKey other) => _encoded.Span.SequenceEqual(other._encoded.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IndexKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_encoded.Span);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Convert.ToHexString(_encoded.Span);
}
