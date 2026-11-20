using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the source of a link: the terminus a link's messages originate from (AMQP 1.0 Part 3,
/// section 3.5.3).
/// </summary>
/// <remarks>
/// The sending endpoint of a link is authoritative for the source. A receiver proposes the source it
/// wants, and the sender's attach carries the source it actually uses; a sender that refuses the link
/// attaches with no source and detaches.
/// </remarks>
public sealed class AmqpSource
{
    /// <summary>
    /// Gets or sets the address of the source node, or <see langword="null"/> when the source is dynamic
    /// and the sender has not yet assigned one.
    /// </summary>
    /// <remarks>
    /// A receiver that opens a link asking for a dynamic source names no address, and the sender that
    /// creates the node names the address it assigned (Part 3, section 3.5.3).
    /// </remarks>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets what state of the source is retained durably. Defaults to <see cref="AmqpTerminusDurability.None"/>.
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
    /// Gets or sets the event that starts the source's expiry timer: one of the policies on
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
    /// Gets or sets how long, in seconds, the source survives once its expiry timer starts. Defaults to 0.
    /// </summary>
    public uint Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source node is created on demand: on a receiver's attach
    /// that opens the link, a request that the sender create it and assign its address; on a sender's
    /// attach, a statement that the sender created it.
    /// </summary>
    /// <remarks>
    /// The sender owns the source, so a sender's dynamic source names the address of the node it created,
    /// and a receiver's request names none; a receiver's answer echoes the source the sender sent
    /// (Part 3, section 3.5.3). A local attach that breaks either rule is refused before it is sent, and a
    /// peer's attach that opens a link and breaks one loses that link.
    /// </remarks>
    public bool Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the properties of the dynamic node, keyed by the names on
    /// <see cref="AmqpNodeProperties"/> and any others, or <see langword="null"/> for none: on a receiver's
    /// attach the properties it wants the node created with, on a sender's those of the node it created.
    /// </summary>
    /// <remarks>
    /// Only a dynamic source carries them (Part 3, sections 3.5.3 and 3.5.9). The
    /// <see cref="AmqpNodeProperties.LifetimePolicy"/> property holds an <see cref="AmqpLifetimePolicy"/>,
    /// or an <see cref="AmqpDescribedValue"/> for a policy an extension defines, and the
    /// <see cref="AmqpNodeProperties.SupportedDistributionModes"/> property holds one symbol or a non-empty
    /// array of them; a <see langword="null"/> value stands for an absent property. A local attach that
    /// breaks a rule is refused before it is sent, and a peer's attach that opens a link and breaks one
    /// loses that link.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <summary>
    /// Gets or sets the distribution mode, such as <see cref="AmqpDistributionMode.Move"/>, or
    /// <see langword="null"/> to leave it to the node.
    /// </summary>
    public AmqpSymbol? DistributionMode { get; set; }

    /// <summary>
    /// Gets or sets the filters the source applies, keyed by filter name, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Each value is a described filter, an <see cref="AmqpDescribedValue"/>, or <see langword="null"/>
    /// (Part 3, section 3.5.8). A local attach whose source carries any other value is refused before it is
    /// sent, and a peer's attach that opens a link with one loses that link.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Filter { get; set; }

    /// <summary>
    /// Gets or sets the outcome a delivery is given when it is settled without one, or
    /// <see langword="null"/> to leave it to the node.
    /// </summary>
    public AmqpOutcome? DefaultOutcome { get; set; }

    /// <summary>
    /// Gets or sets the descriptor names of the outcomes the source supports, such as
    /// <see cref="AmqpAccepted.Descriptor"/> and <see cref="AmqpReleased.Descriptor"/>, or
    /// <see langword="null"/> for the node's defaults.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Outcomes { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities the source supports or desires, or <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
