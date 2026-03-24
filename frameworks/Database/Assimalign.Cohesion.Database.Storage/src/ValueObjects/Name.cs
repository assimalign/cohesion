using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A strongly-typed name for a storage resource, segment, or other named entity.
/// </summary>
public readonly struct Name : IEquatable<Name>
{
    private readonly string? _value;

    /// <summary>
    /// Initializes a new <see cref="Name"/> with the specified string value.
    /// </summary>
    /// <param name="value">The name string.</param>
    public Name(string value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the length of the name in characters.
    /// </summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// Gets a value indicating whether the name is empty or unset.
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <inheritdoc />
    public bool Equals(Name other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Name other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public override string ToString() => _value ?? string.Empty;

    /// <summary>
    /// Implicitly converts a <see cref="Name"/> to a <see cref="string"/>.
    /// </summary>
    public static implicit operator string(Name name) => name._value ?? string.Empty;

    /// <summary>
    /// Implicitly converts a <see cref="string"/> to a <see cref="Name"/>.
    /// </summary>
    public static implicit operator Name(string value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Name left, Name right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Name left, Name right) => !left.Equals(right);
}
