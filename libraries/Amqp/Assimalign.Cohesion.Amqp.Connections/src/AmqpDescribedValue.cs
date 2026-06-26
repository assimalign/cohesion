using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents a generic AMQP described value.
/// </summary>
public sealed class AmqpDescribedValue
{
    /// <summary>
    /// Initializes a new described value.
    /// </summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="value">The described value.</param>
    public AmqpDescribedValue(object descriptor, object? value)
    {
        Descriptor = descriptor;
        Value = value;
    }

    /// <summary>
    /// Gets the AMQP descriptor.
    /// </summary>
    public object Descriptor { get; }

    /// <summary>
    /// Gets the AMQP described value.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Creates the standard AMQP accepted delivery state.
    /// </summary>
    public static AmqpDescribedValue Accepted()
    {
        return new AmqpDescribedValue(0x24ul, System.Array.Empty<object?>());
    }

    /// <summary>
    /// Creates the standard AMQP released delivery state.
    /// </summary>
    public static AmqpDescribedValue Released()
    {
        return new AmqpDescribedValue(0x26ul, System.Array.Empty<object?>());
    }

    /// <summary>
    /// Creates the standard AMQP rejected delivery state.
    /// </summary>
    /// <param name="error">The rejection error.</param>
    public static AmqpDescribedValue Rejected(AmqpError? error = null)
    {
        return new AmqpDescribedValue(0x25ul, new object?[] { error });
    }

    /// <summary>
    /// Creates the standard AMQP received delivery state.
    /// </summary>
    /// <param name="sectionNumber">The received section number.</param>
    /// <param name="sectionOffset">The received section offset.</param>
    public static AmqpDescribedValue Received(uint sectionNumber, ulong sectionOffset)
    {
        return new AmqpDescribedValue(0x23ul, new object?[] { sectionNumber, sectionOffset });
    }

    /// <summary>
    /// Creates the standard AMQP modified delivery state.
    /// </summary>
    /// <param name="deliveryFailed">Indicates whether the delivery failed.</param>
    /// <param name="undeliverableHere">Indicates whether the delivery is undeliverable here.</param>
    /// <param name="messageAnnotations">The replacement message annotations.</param>
    public static AmqpDescribedValue Modified(
        bool? deliveryFailed = null,
        bool? undeliverableHere = null,
        IReadOnlyDictionary<AmqpSymbol, object?>? messageAnnotations = null)
    {
        return new AmqpDescribedValue(0x27ul, new object?[] { deliveryFailed, undeliverableHere, messageAnnotations });
    }
}
