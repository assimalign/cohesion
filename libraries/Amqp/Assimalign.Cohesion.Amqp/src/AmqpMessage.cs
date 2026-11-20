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
/// Annotation keys are symbols. AMQP also reserves unsigned-long annotation keys for future use; the codec
/// refuses them, since no current specification assigns one.
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
