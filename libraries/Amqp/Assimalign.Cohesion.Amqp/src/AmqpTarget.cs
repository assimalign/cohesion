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
    /// <remarks>
    /// A sender that opens a link asking for a dynamic target names no address, and the receiver that
    /// creates the node names the address it assigned (Part 3, section 3.5.4).
    /// </remarks>
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
    /// Gets or sets a value indicating whether the target node is created on demand: on a sender's attach
    /// that opens the link, a request that the receiver create it and assign its address; on a receiver's
    /// attach, a statement that the receiver created it.
    /// </summary>
    /// <remarks>
    /// The receiver owns the target, so a receiver's dynamic target names the address of the node it
    /// created, and a sender's request names none; a sender's answer echoes the target the receiver sent
    /// (Part 3, section 3.5.4). A local attach that breaks either rule is refused before it is sent, and a
    /// peer's attach that opens a link and breaks one loses that link.
    /// </remarks>
    public bool Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the properties of the dynamic node, keyed by the names on
    /// <see cref="AmqpNodeProperties"/> and any others, or <see langword="null"/> for none: on a sender's
    /// attach the properties it wants the node created with, on a receiver's those of the node it created.
    /// </summary>
    /// <remarks>
    /// Only a dynamic target carries them (Part 3, sections 3.5.4 and 3.5.9). The
    /// <see cref="AmqpNodeProperties.LifetimePolicy"/> property holds an <see cref="AmqpLifetimePolicy"/>,
    /// or an <see cref="AmqpDescribedValue"/> for a policy an extension defines, and the
    /// <see cref="AmqpNodeProperties.SupportedDistributionModes"/> property holds one symbol or a non-empty
    /// array of them; a <see langword="null"/> value stands for an absent property. A local attach that
    /// breaks a rule is refused before it is sent, and a peer's attach that opens a link and breaks one
    /// loses that link.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
