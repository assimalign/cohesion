using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// A strongly-typed name for a logical database instance.
/// </summary>
public readonly struct DatabaseName : IEquatable<DatabaseName>
{
    private readonly string? _value;

    /// <summary>
    /// Initializes a new <see cref="DatabaseName"/> with the specified string value.
    /// </summary>
    /// <param name="value">The database name string.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null or empty.</exception>
    public DatabaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(value));
        }

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
    public bool Equals(DatabaseName other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DatabaseName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;

    /// <inheritdoc />
    public override string ToString() => _value ?? string.Empty;

    /// <summary>
    /// Implicitly converts a <see cref="DatabaseName"/> to a <see cref="string"/>.
    /// </summary>
    public static implicit operator string(DatabaseName name) => name._value ?? string.Empty;

    /// <summary>
    /// Implicitly converts a <see cref="string"/> to a <see cref="DatabaseName"/>.
    /// </summary>
    public static implicit operator DatabaseName(string value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DatabaseName left, DatabaseName right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DatabaseName left, DatabaseName right) => !left.Equals(right);
}
