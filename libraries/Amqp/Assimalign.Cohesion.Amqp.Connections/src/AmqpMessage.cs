using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an AMQP 1.0 message composed from standard message sections.
/// </summary>
public sealed class AmqpMessage
{
    /// <summary>
    /// Gets or sets the message durability flag from the header section.
    /// </summary>
    public bool? Durable { get; set; }

    /// <summary>
    /// Gets or sets the message priority from the header section.
    /// </summary>
    public byte? Priority { get; set; }

    /// <summary>
    /// Gets or sets the message time-to-live from the header section.
    /// </summary>
    public uint? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the first-acquirer flag from the header section.
    /// </summary>
    public bool? FirstAcquirer { get; set; }

    /// <summary>
    /// Gets or sets the delivery count from the header section.
    /// </summary>
    public uint? DeliveryCount { get; set; }

    /// <summary>
    /// Gets or sets the delivery annotations section.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DeliveryAnnotations { get; set; }

    /// <summary>
    /// Gets or sets the message annotations section.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? MessageAnnotations { get; set; }

    /// <summary>
    /// Gets or sets the AMQP message-id property.
    /// </summary>
    public object? MessageId { get; set; }

    /// <summary>
    /// Gets or sets the AMQP user-id property.
    /// </summary>
    public ReadOnlyMemory<byte>? UserId { get; set; }

    /// <summary>
    /// Gets or sets the AMQP to property.
    /// </summary>
    public string? To { get; set; }

    /// <summary>
    /// Gets or sets the AMQP subject property.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the AMQP reply-to property.
    /// </summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Gets or sets the AMQP correlation-id property.
    /// </summary>
    public object? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the AMQP content-type property.
    /// </summary>
    public AmqpSymbol? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the AMQP content-encoding property.
    /// </summary>
    public AmqpSymbol? ContentEncoding { get; set; }

    /// <summary>
    /// Gets or sets the AMQP absolute-expiry-time property.
    /// </summary>
    public DateTimeOffset? AbsoluteExpiryTime { get; set; }

    /// <summary>
    /// Gets or sets the AMQP creation-time property.
    /// </summary>
    public DateTimeOffset? CreationTime { get; set; }

    /// <summary>
    /// Gets or sets the AMQP group-id property.
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Gets or sets the AMQP group-sequence property.
    /// </summary>
    public uint? GroupSequence { get; set; }

    /// <summary>
    /// Gets or sets the AMQP reply-to-group-id property.
    /// </summary>
    public string? ReplyToGroupId { get; set; }

    /// <summary>
    /// Gets or sets the application-properties section.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ApplicationProperties { get; set; }

    /// <summary>
    /// Gets or sets the data sections.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? DataSections { get; set; }

    /// <summary>
    /// Gets or sets the AMQP sequence sections.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>>? SequenceSections { get; set; }

    /// <summary>
    /// Gets or sets the AMQP value section.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets the footer section.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Footer { get; set; }
}
