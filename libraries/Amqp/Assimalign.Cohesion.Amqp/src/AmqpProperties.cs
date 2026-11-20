using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the properties section of a message: its immutable standard properties (AMQP 1.0 Part 3,
/// section 3.2.4).
/// </summary>
[DebuggerDisplay("MessageId = {MessageId}, To = {To,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpProperties
{
    private object? _messageId;
    private object? _correlationId;

    /// <summary>
    /// Gets or sets the identifier that uniquely identifies the message, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// AMQP restricts identifiers to four types: <see cref="ulong"/>, <see cref="Guid"/>, binary data
    /// (a <see cref="byte"/> array or <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/>) and
    /// <see cref="string"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the assigned value is not one of the identifier types.</exception>
    public object? MessageId
    {
        get => _messageId;
        set => _messageId = ValidateId(value);
    }

    /// <summary>
    /// Gets or sets the identity of the user who produced the message, or <see langword="null"/>.
    /// </summary>
    public ReadOnlyMemory<byte>? UserId { get; set; }

    /// <summary>
    /// Gets or sets the address of the node the message is destined for, or <see langword="null"/>.
    /// </summary>
    public string? To { get; set; }

    /// <summary>
    /// Gets or sets an application-specific summary of the message, or <see langword="null"/>.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the address of the node replies are sent to, or <see langword="null"/>.
    /// </summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Gets or sets the identifier that relates this message to another, such as the request a reply
    /// answers, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The identifier types are those of <see cref="MessageId"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the assigned value is not one of the identifier types.</exception>
    public object? CorrelationId
    {
        get => _correlationId;
        set => _correlationId = ValidateId(value);
    }

    /// <summary>
    /// Gets or sets the MIME type of the message body, such as <c>application/json</c>, or <see langword="null"/>.
    /// </summary>
    public AmqpSymbol? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the encoding applied to the body on top of its content type, such as <c>gzip</c>, or
    /// <see langword="null"/>.
    /// </summary>
    public AmqpSymbol? ContentEncoding { get; set; }

    /// <summary>
    /// Gets or sets the time after which the message is considered expired, or <see langword="null"/>.
    /// </summary>
    /// <remarks>AMQP timestamps have millisecond precision.</remarks>
    public DateTimeOffset? AbsoluteExpiryTime { get; set; }

    /// <summary>
    /// Gets or sets the time the message was created, or <see langword="null"/>.
    /// </summary>
    /// <remarks>AMQP timestamps have millisecond precision.</remarks>
    public DateTimeOffset? CreationTime { get; set; }

    /// <summary>
    /// Gets or sets the group the message belongs to, or <see langword="null"/>.
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Gets or sets the message's position within its group, or <see langword="null"/>.
    /// </summary>
    public uint? GroupSequence { get; set; }

    /// <summary>
    /// Gets or sets the group replies are sent to, or <see langword="null"/>.
    /// </summary>
    public string? ReplyToGroupId { get; set; }

    private static object? ValidateId(object? value)
    {
        // Binary is matched by exact type: CLR array covariance lets an sbyte[] pass a byte[] pattern, and
        // it would reach the wire as an array, not binary.
        return value switch
        {
            null or ulong or Guid or string or ReadOnlyMemory<byte> => value,
            byte[] bytes when bytes.GetType() == typeof(byte[]) => value,
            _ => throw new ArgumentException(ErrorMessages.MessageIdTypeInvalid, nameof(value))
        };
    }

    private sealed class DebugView
    {
        private readonly AmqpProperties _properties;

        public DebugView(AmqpProperties properties)
        {
            _properties = properties;
        }

        public object? MessageId => _properties.MessageId;
        public ReadOnlyMemory<byte>? UserId => _properties.UserId;
        public string? To => _properties.To;
        public string? Subject => _properties.Subject;
        public string? ReplyTo => _properties.ReplyTo;
        public object? CorrelationId => _properties.CorrelationId;
        public AmqpSymbol? ContentType => _properties.ContentType;
        public AmqpSymbol? ContentEncoding => _properties.ContentEncoding;
        public DateTimeOffset? AbsoluteExpiryTime => _properties.AbsoluteExpiryTime;
        public DateTimeOffset? CreationTime => _properties.CreationTime;
        public string? GroupId => _properties.GroupId;
        public uint? GroupSequence => _properties.GroupSequence;
        public string? ReplyToGroupId => _properties.ReplyToGroupId;
    }
}
