using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an AMQP 1.0 message: its sections, in the order the format defines them (AMQP 1.0 Part 3,
/// section 3.2).
/// </summary>
/// <remarks>
/// <para>
/// A section left <see langword="null"/> is omitted when the message is encoded. The format fixes the
/// order: header, delivery annotations, message annotations, properties, application properties, body
/// and footer; each appears at most once.
/// </para>
/// <para>
/// Annotation keys are symbols (Part 3, section 3.2.10). AMQP also reserves unsigned-long keys, which no
/// specification assigns yet; the codec understands none, so a received message that carries one fails
/// to decode with <c>amqp:not-implemented</c>, the condition a receiver detaches the link with when it
/// meets a key it does not understand. Symbolic keys that do not begin with <c>x-</c> are reserved too,
/// but the codec keeps them in both directions: an intermediary forwards what it received, and only the
/// application knows which keys it understands. A receiving application must detach the link with
/// <c>amqp:not-implemented</c> on a key it does not understand unless the key begins with <c>x-opt</c>.
/// </para>
/// </remarks>
public sealed class AmqpMessage
{
    /// <summary>
    /// Gets or sets the header section, or <see langword="null"/> to omit it.
    /// </summary>
    public AmqpHeader? Header { get; set; }

    /// <summary>
    /// Gets or sets the delivery annotations: properties meant for the next hop only, or
    /// <see langword="null"/> to omit them.
    /// </summary>
    /// <remarks>
    /// AMQP reserves the key <c>rejected</c> for an <see cref="AmqpError"/> that tells why a message was
    /// rejected (Part 3, section 3.2.2): a message whose <c>rejected</c> annotation holds anything else is
    /// refused when it is encoded, and kept as it arrives when it is decoded.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DeliveryAnnotations { get; set; }

    /// <summary>
    /// Gets or sets the message annotations: properties intermediaries add or read, or
    /// <see langword="null"/> to omit them.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? MessageAnnotations { get; set; }

    /// <summary>
    /// Gets or sets the properties section, or <see langword="null"/> to omit it.
    /// </summary>
    public AmqpProperties? Properties { get; set; }

    /// <summary>
    /// Gets or sets the application properties, or <see langword="null"/> to omit them.
    /// </summary>
    /// <remarks>
    /// Keys are strings, and AMQP restricts values to simple types: a value cannot be a list, a map or an
    /// array, bare or described (Part 3, section 3.2.5). A message whose properties break the rule is
    /// refused when it is encoded; one received with such a value keeps it, as other libraries do.
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? ApplicationProperties { get; set; }

    /// <summary>
    /// Gets or sets the body, or <see langword="null"/> for a message without one.
    /// </summary>
    /// <remarks>
    /// The AMQP grammar always gives a message a body, but peers that follow the AMQP JMS mapping send
    /// messages without one; the codec accepts and produces both.
    /// </remarks>
    public AmqpMessageBody? Body { get; set; }

    /// <summary>
    /// Gets or sets the footer: details computed over the bare message, such as hashes and signatures, or
    /// <see langword="null"/> to omit it.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Footer { get; set; }
}
