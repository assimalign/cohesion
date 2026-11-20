using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an AMQP 1.0 described value whose descriptor no typed model in this library claims.
/// </summary>
/// <remarks>
/// AMQP 1.0 Part 1, section 1.2 lets any value carry a descriptor that annotates it with a semantic
/// type. The codec decodes the described types this library models (performatives, message sections,
/// termini, delivery states and errors) into their own types, and every other described value into a
/// <see cref="AmqpDescribedValue"/>, so a filter, a vendor annotation or a future type round-trips
/// unchanged.
/// </remarks>
public sealed class AmqpDescribedValue
{
    /// <summary>
    /// Initializes a new described value.
    /// </summary>
    /// <param name="descriptor">
    /// The descriptor: an <see cref="AmqpSymbol"/> name or a <see cref="ulong"/> code. A <see cref="string"/>
    /// is accepted for a symbolic descriptor and converted to an <see cref="AmqpSymbol"/>.
    /// </param>
    /// <param name="value">The described value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="descriptor"/> is neither a symbol nor an unsigned long.</exception>
    public AmqpDescribedValue(object descriptor, object? value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor switch
        {
            AmqpSymbol symbol => symbol,
            ulong code => code,
            string name => new AmqpSymbol(name),
            _ => throw new ArgumentException(ErrorMessages.DescriptorTypeInvalid, nameof(descriptor))
        };
        Value = value;
    }

    /// <summary>
    /// Gets the descriptor: an <see cref="AmqpSymbol"/> or a <see cref="ulong"/>.
    /// </summary>
    public object Descriptor { get; }

    /// <summary>
    /// Gets the described value.
    /// </summary>
    public object? Value { get; }
}
