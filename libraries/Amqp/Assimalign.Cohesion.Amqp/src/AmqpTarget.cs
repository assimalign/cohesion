using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a messaging-node target: the terminus a link's messages are delivered to (AMQP 1.0 Part 3,
/// section 3.5.4).
/// </summary>
/// <remarks>
/// The receiving endpoint of a link is authoritative for the target. A sender proposes the target it
/// wants, and the receiver's attach carries the target it actually uses; a receiver that refuses the
/// link attaches with no target and detaches.
/// </remarks>
public sealed class AmqpTarget : IAmqpTarget
{
    /// <summary>
    /// Gets or sets the address of the target node, or <see langword="null"/> when the target is dynamic
    /// and the receiver has not yet assigned one, or when each message names its own destination.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets what state of the target is retained durably. Defaults to <see cref="AmqpTerminusDurability.None"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not one AMQP 1.0 defines (Part 3, section 3.5.5).</exception>
    public AmqpTerminusDurability Durable
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the event that starts the target's expiry timer: one of the policies on
    /// <see cref="AmqpTerminusExpiryPolicy"/>. Defaults to <see cref="AmqpTerminusExpiryPolicy.SessionEnd"/>,
    /// which the wire also encodes by omission.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value is not one of the policies AMQP 1.0 defines (Part 3, section 3.5.6).</exception>
    public AmqpSymbol ExpiryPolicy
    {
        get;
        set
        {
            if (value != AmqpTerminusExpiryPolicy.LinkDetach
                && value != AmqpTerminusExpiryPolicy.SessionEnd
                && value != AmqpTerminusExpiryPolicy.ConnectionClose
                && value != AmqpTerminusExpiryPolicy.Never)
            {
                throw new ArgumentException(ErrorMessages.ExpiryPolicyInvalid, nameof(value));
            }

            field = value;
        }
    } = AmqpTerminusExpiryPolicy.SessionEnd;

    /// <summary>
    /// Gets or sets how long, in seconds, the target survives once its expiry timer starts. Defaults to 0.
    /// </summary>
    public uint Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sender asks the receiver to create the target node on
    /// demand and assign its address.
    /// </summary>
    public bool Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the properties of the dynamically created node, or <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
