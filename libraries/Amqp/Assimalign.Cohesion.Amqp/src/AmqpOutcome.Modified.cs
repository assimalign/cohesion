using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>modified</c> outcome: the receiver did not process the message, and asks the source
/// to change it before any redelivery (AMQP 1.0 Part 3, section 3.4.5).
/// </summary>
public sealed class AmqpModified : AmqpOutcome
{
    /// <summary>
    /// Initializes a new modified outcome.
    /// </summary>
    /// <param name="deliveryFailed">Whether the source counts the attempt as a failed delivery, incrementing the message's delivery count.</param>
    /// <param name="undeliverableHere">Whether the source must not redeliver the message to this link.</param>
    /// <param name="messageAnnotations">The message annotations the source merges into the message, or <see langword="null"/>.</param>
    public AmqpModified(
        bool deliveryFailed = false,
        bool undeliverableHere = false,
        IReadOnlyDictionary<AmqpSymbol, object?>? messageAnnotations = null)
    {
        DeliveryFailed = deliveryFailed;
        UndeliverableHere = undeliverableHere;
        MessageAnnotations = messageAnnotations;
    }

    /// <summary>
    /// Gets a value indicating whether the source counts the attempt as a failed delivery.
    /// </summary>
    public bool DeliveryFailed { get; }

    /// <summary>
    /// Gets a value indicating whether the source must not redeliver the message to this link.
    /// </summary>
    public bool UndeliverableHere { get; }

    /// <summary>
    /// Gets the message annotations the source merges into the message, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? MessageAnnotations { get; }

    /// <summary>
    /// Returns the outcome's name.
    /// </summary>
    /// <returns>The text <c>modified</c>.</returns>
    public override string ToString()
    {
        return "modified";
    }
}
