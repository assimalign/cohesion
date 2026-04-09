using System;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP symbol value.
/// </summary>
public readonly struct AmqpSymbol : IEquatable<AmqpSymbol>
{
    /// <summary>
    /// Initializes a new AMQP symbol.
    /// </summary>
    /// <param name="value">The symbol value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public AmqpSymbol(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>
    /// Gets the symbol value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AmqpSymbol other && Equals(other);
    }

    /// <inheritdoc />
    public bool Equals(AmqpSymbol other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    /// <summary>
    /// Converts a string to an AMQP symbol.
    /// </summary>
    public static implicit operator AmqpSymbol(string value)
    {
        return new(value);
    }

    /// <summary>
    /// Converts an AMQP symbol to a string.
    /// </summary>
    public static implicit operator string(AmqpSymbol value)
    {
        return value.Value;
    }

    /// <summary>
    /// Compares two AMQP symbols for equality.
    /// </summary>
    public static bool operator ==(AmqpSymbol left, AmqpSymbol right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two AMQP symbols for inequality.
    /// </summary>
    public static bool operator !=(AmqpSymbol left, AmqpSymbol right)
    {
        return !left.Equals(right);
    }
}
