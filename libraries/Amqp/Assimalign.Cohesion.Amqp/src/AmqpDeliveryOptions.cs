namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Configures one delivery a sender sends: its tag, message format, settlement and initial state
/// (AMQP 1.0 Part 2, section 2.7.5).
/// </summary>
public sealed class AmqpDeliveryOptions
{
    /// <summary>
    /// Initializes new delivery options with the defaults.
    /// </summary>
    public AmqpDeliveryOptions()
    {
    }

    /// <summary>
    /// Gets or sets the delivery tag, or <see langword="null"/> to have the sender generate one that is
    /// unique on its link.
    /// </summary>
    /// <remarks>
    /// A tag of the application's choosing must be unique among the link's unsettled deliveries, except when
    /// <see cref="Resume"/> resends a delivery from an earlier attachment of the link.
    /// </remarks>
    public AmqpDeliveryTag? DeliveryTag { get; set; }

    /// <summary>
    /// Gets or sets the message format of the payload. Defaults to 0, the AMQP 1.0 message format.
    /// </summary>
    public uint MessageFormat { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the delivery is sent settled, or <see langword="null"/> to
    /// follow the link's sender settlement mode.
    /// </summary>
    /// <remarks>
    /// Under <see cref="AmqpSenderSettleMode.Mixed"/>, <see langword="null"/> sends the delivery unsettled. Under
    /// <see cref="AmqpSenderSettleMode.Settled"/> or <see cref="AmqpSenderSettleMode.Unsettled"/>, a value that
    /// contradicts the mode is refused.
    /// </remarks>
    public bool? Settled { get; set; }

    /// <summary>
    /// Gets or sets the delivery state the first <c>transfer</c> frame carries, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A transactional send carries an <see cref="AmqpTransactionalState"/> naming the transaction
    /// (AMQP 1.0 Part 4, section 4.4.1). A resumed delivery carries the state the sender last knew, such
    /// as an <see cref="AmqpReceived"/> that tells the receiver where to resume.
    /// </remarks>
    public AmqpDeliveryState? State { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the delivery resumes one from an earlier attachment of the
    /// link, under the same <see cref="DeliveryTag"/> (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    /// <remarks>
    /// The peer's <see cref="IAmqpLink.RemoteUnsettled"/> map tells the sender which deliveries the receiver
    /// still holds and in what state. A resumed delivery requires an explicit <see cref="DeliveryTag"/>.
    /// </remarks>
    public bool Resume { get; set; }
}
