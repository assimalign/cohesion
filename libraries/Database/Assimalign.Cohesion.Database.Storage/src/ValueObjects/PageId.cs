using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A strongly-typed identifier for a page within a storage file.
/// </summary>
public readonly struct PageId : IEquatable<PageId>, IComparable<PageId>
{
    private readonly long _value;

    /// <summary>
    /// Initializes a new <see cref="PageId"/> with the specified value.
    /// </summary>
    /// <param name="value">The underlying page number.</param>
    public PageId(long value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying numeric value of this page identifier.
    /// </summary>
    public long Value => _value;

    /// <inheritdoc />
    public bool Equals(PageId other) => _value == other._value;

    /// <inheritdoc />
    public int CompareTo(PageId other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PageId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Implicitly converts a <see cref="PageId"/> to a <see cref="long"/>.
    /// </summary>
    public static implicit operator long(PageId id) => id._value;

    /// <summary>
    /// Implicitly converts a <see cref="long"/> to a <see cref="PageId"/>.
    /// </summary>
    public static implicit operator PageId(long value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PageId left, PageId right) => left._value == right._value;

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PageId left, PageId right) => left._value != right._value;
}
