using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A multi-level hierarchical address used to locate a segment within the storage
/// file's segment tree. Each level represents a byte offset relative to its parent.
/// </summary>
/// <remarks>
/// Addresses form a path from the root segment to a target segment:
/// <code>
/// Depth 0: Root segment stream position (absolute offset)
/// Depth 1: Root offset + child offset
/// Depth 2: Depth 1 offset + grandchild offset
/// ... and so on
/// </code>
/// </remarks>
public readonly struct Address : IEquatable<Address>
{
    private readonly long[]? _offsets;

    /// <summary>
    /// Initializes a new <see cref="Address"/> with the specified offset path.
    /// </summary>
    /// <param name="offsets">The offset at each depth level.</param>
    public Address(params long[] offsets)
    {
        _offsets = offsets;
    }

    /// <summary>
    /// Gets an empty address with no depth.
    /// </summary>
    public static Address Empty => new(Array.Empty<long>());

    /// <summary>
    /// Gets the depth (number of levels) of this address.
    /// </summary>
    public int Depth => _offsets?.Length ?? 0;

    /// <summary>
    /// Gets the byte offset at the specified depth level.
    /// </summary>
    /// <param name="depth">The zero-based depth level.</param>
    /// <returns>The byte offset at the specified level.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The depth is out of range.</exception>
    public long GetOffset(int depth)
    {
        if (_offsets is null || (uint)depth >= (uint)_offsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        return _offsets[depth];
    }

    /// <summary>
    /// Creates a new address by appending an additional offset level.
    /// </summary>
    /// <param name="offset">The byte offset for the new depth level.</param>
    /// <returns>A new address with an additional depth level.</returns>
    public Address Append(long offset)
    {
        var currentLength = _offsets?.Length ?? 0;
        var newOffsets = new long[currentLength + 1];
        _offsets?.CopyTo(newOffsets, 0);
        newOffsets[currentLength] = offset;
        return new Address(newOffsets);
    }

    /// <summary>
    /// Creates a root-level address with a single offset.
    /// </summary>
    /// <param name="offset">The root segment stream position.</param>
    /// <returns>A single-level address.</returns>
    public static Address Root(long offset) => new(offset);

    /// <summary>
    /// Computes the absolute stream position by summing all offset levels.
    /// </summary>
    /// <returns>The total absolute byte offset.</returns>
    public long ToAbsoluteOffset()
    {
        if (_offsets is null)
        {
            return 0;
        }

        long total = 0;
        for (int i = 0; i < _offsets.Length; i++)
        {
            total += _offsets[i];
        }
        return total;
    }

    /// <inheritdoc />
    public bool Equals(Address other)
    {
        if (Depth != other.Depth)
        {
            return false;
        }
        for (int i = 0; i < Depth; i++)
        {
            if (_offsets![i] != other._offsets![i])
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Address other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (_offsets is null)
        {
            return 0;
        }

        var hash = new HashCode();
        for (int i = 0; i < _offsets.Length; i++)
        {
            hash.Add(_offsets[i]);
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (_offsets is null || _offsets.Length == 0)
        {
            return "<empty>";
        }
        return string.Join(" -> ", _offsets);
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Address left, Address right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Address left, Address right) => !left.Equals(right);
}
