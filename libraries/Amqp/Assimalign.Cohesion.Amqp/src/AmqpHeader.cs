namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the header section of a message: the delivery details intermediaries read and may change
/// (AMQP 1.0 Part 3, section 3.2.1).
/// </summary>
/// <remarks>
/// Every member defaults to the value AMQP gives the field when the section omits it, and the codec omits
/// a field that holds its default.
/// </remarks>
public sealed class AmqpHeader
{
    /// <summary>
    /// Gets or sets a value indicating whether intermediaries must keep the message safe across their own
    /// failures. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Durable { get; set; }

    /// <summary>
    /// Gets or sets the message's relative priority, where a higher number is more urgent. Defaults to 4.
    /// </summary>
    public byte Priority { get; set; } = 4;

    /// <summary>
    /// Gets or sets how long, in milliseconds, the message is considered live after an intermediary first
    /// receives it, or <see langword="null"/> when it does not expire.
    /// </summary>
    public uint? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether no earlier acquirer has been offered the message.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool FirstAcquirer { get; set; }

    /// <summary>
    /// Gets or sets the number of unsuccessful delivery attempts before this one. Defaults to 0.
    /// </summary>
    public uint DeliveryCount { get; set; }
}
