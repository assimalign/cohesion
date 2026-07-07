using System;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A strongly-typed document identity, unique within a collection.
/// </summary>
/// <remarks>
/// Identities are ordinal, case-sensitive strings (for example <c>orders/2026/1042</c>),
/// so range scans over identity prefixes are meaningful.
/// </remarks>
public readonly struct DocumentId : IEquatable<DocumentId>
{
    private readonly string? _value;

    /// <summary>
    /// Initializes a new <see cref="DocumentId"/> with the specified value.
    /// </summary>
    /// <param name="value">The identity string.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null or empty.</exception>
    public DocumentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Document id cannot be null or empty.", nameof(value));
        }
        _value = value;
    }

    /// <summary>
    /// Gets the underlying identity string.
    /// </summary>
    public string Value => _value ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(DocumentId other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DocumentId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Implicitly converts a string to a <see cref="DocumentId"/>.
    /// </summary>
    public static implicit operator DocumentId(string value) => new(value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DocumentId left, DocumentId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DocumentId left, DocumentId right) => !left.Equals(right);
}
