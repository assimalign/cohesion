using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// A strongly-typed unique identifier for a database transaction, backed by a <see cref="Guid"/>.
/// </summary>
public readonly struct TransactionId : IEquatable<TransactionId>
{
    private readonly Guid _value;

    /// <summary>
    /// Initializes a new <see cref="TransactionId"/> with the specified GUID.
    /// </summary>
    /// <param name="value">The underlying GUID value.</param>
    public TransactionId(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying GUID value.
    /// </summary>
    public Guid Value => _value;

    /// <summary>
    /// Creates a new <see cref="TransactionId"/> with a randomly generated GUID.
    /// </summary>
    /// <returns>A new unique transaction identifier.</returns>
    public static TransactionId NewId() => new(Guid.NewGuid());

    /// <inheritdoc />
    public bool Equals(TransactionId other) => _value.Equals(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TransactionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Implicitly converts a <see cref="TransactionId"/> to a <see cref="Guid"/>.
    /// </summary>
    public static implicit operator Guid(TransactionId id) => id._value;

    /// <summary>
    /// Implicitly converts a <see cref="Guid"/> to a <see cref="TransactionId"/>.
    /// </summary>
    public static implicit operator TransactionId(Guid value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TransactionId left, TransactionId right) => left._value == right._value;

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TransactionId left, TransactionId right) => left._value != right._value;
}
