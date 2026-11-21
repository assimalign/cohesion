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
    /// <para>
    /// A tag must be unique among the deliveries either end of the link may consider unsettled (AMQP 1.0
    /// Part 2, section 2.6.12), so a tag of the application's choosing is not one of the link's unsettled
    /// deliveries, except when <see cref="Resume"/> resumes a delivery from an earlier attachment of the link.
    /// A new delivery cannot take the tag of one the receiver waits to have resumed.
    /// </para>
    /// <para>
    /// A generated tag skips every tag either unsettled map of the link's attach listed, since a delivery from
    /// an earlier attachment may still be unsettled at one end.
    /// </para>
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
    /// <para>
    /// A transactional send carries an <see cref="AmqpTransactionalState"/> naming the transaction
    /// (AMQP 1.0 Part 4, section 4.4.1). Every frame of a split delivery must name the same transaction, so
    /// a transactional state goes on each of its frames, an aborting one included. A link to a transaction
    /// coordinator refuses one: the control link carries no transactional work (section 4.1).
    /// </para>
    /// <para>
    /// A resumed delivery carries the state the sender last knew: an <see cref="AmqpReceived"/> that names the
    /// section and offset the payload starts at, or the terminal outcome at the sender (AMQP 1.0 Part 3,
    /// section 3.4.6). Only a resumed delivery may carry an <see cref="AmqpReceived"/>, which goes on its first
    /// frame alone (section 3.4.1).
    /// </para>
    /// </remarks>
    public AmqpDeliveryState? State { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the delivery resumes one from an earlier attachment of the
    /// link, under the same <see cref="DeliveryTag"/> (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resumed delivery requires an explicit <see cref="DeliveryTag"/> that the link's
    /// <see cref="IAmqpLink.UnsettledReconciliation"/> lists as resumable, and each such delivery is resumed
    /// once per attachment. The peer's <see cref="IAmqpLink.RemoteUnsettled"/> map tells the sender the state
    /// the receiver holds it in, and so where to resume it from.
    /// </para>
    /// <para>
    /// A resumed delivery may carry no payload when the receiver already has all of it, to convey only the
    /// sender's terminal state and settlement. A delivery the sender cannot resume to completion is aborted
    /// with <see cref="IAmqpSender.AbortDeliveryAsync(AmqpDeliveryTag, System.Threading.CancellationToken)"/>.
    /// </para>
    /// </remarks>
    public bool Resume { get; set; }
}
