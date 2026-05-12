using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A strongly-typed unique identifier for a storage resource, backed by a <see cref="Guid"/>.
/// </summary>
public readonly struct StorageId : IEquatable<StorageId>
{
    private readonly Guid _value;

    /// <summary>
    /// Initializes a new <see cref="StorageId"/> with the specified GUID.
    /// </summary>
    /// <param name="value">The underlying GUID value.</param>
    public StorageId(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying GUID value.
    /// </summary>
    public Guid Value => _value;

    /// <summary>
    /// Creates a new <see cref="StorageId"/> with a randomly generated GUID.
    /// </summary>
    /// <returns>A new unique storage identifier.</returns>
    public static StorageId NewId() => new(Guid.NewGuid());

    /// <inheritdoc />
    public bool Equals(StorageId other) => _value.Equals(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StorageId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Implicitly converts a <see cref="StorageId"/> to a <see cref="Guid"/>.
    /// </summary>
    public static implicit operator Guid(StorageId id) => id._value;

    /// <summary>
    /// Implicitly converts a <see cref="Guid"/> to a <see cref="StorageId"/>.
    /// </summary>
    public static implicit operator StorageId(Guid value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StorageId left, StorageId right) => left._value == right._value;

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StorageId left, StorageId right) => left._value != right._value;
}
