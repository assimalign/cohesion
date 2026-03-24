using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a unique transaction identifier in the journal.
/// </summary>
public readonly struct JournalTransactionId : IEquatable<JournalTransactionId>
{
    private readonly Guid _value;

    /// <summary>
    /// Initializes a new <see cref="JournalTransactionId"/>.
    /// </summary>
    /// <param name="value">Underlying GUID value.</param>
    public JournalTransactionId(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the underlying GUID value.
    /// </summary>
    public Guid Value => _value;

    /// <summary>
    /// Creates a new transaction identifier.
    /// </summary>
    /// <returns>A new <see cref="JournalTransactionId"/>.</returns>
    public static JournalTransactionId NewId() => new(Guid.NewGuid());

    /// <inheritdoc />
    public bool Equals(JournalTransactionId other) => _value.Equals(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JournalTransactionId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Implicitly converts to <see cref="Guid"/>.
    /// </summary>
    public static implicit operator Guid(JournalTransactionId id) => id._value;

    /// <summary>
    /// Implicitly converts from <see cref="Guid"/>.
    /// </summary>
    public static implicit operator JournalTransactionId(Guid value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(JournalTransactionId left, JournalTransactionId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(JournalTransactionId left, JournalTransactionId right) => !left.Equals(right);
}
