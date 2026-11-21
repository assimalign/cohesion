namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Configures a receiving link: the settings
/// <see cref="IAmqpSession.AttachReceiverAsync(AmqpReceiverOptions, System.Threading.CancellationToken)"/>
/// attaches with, or <see cref="IAmqpLinkRequest.AcceptReceiverAsync(AmqpReceiverOptions?, System.Threading.CancellationToken)"/>
/// answers with.
/// </summary>
public sealed class AmqpReceiverOptions : AmqpLinkOptions
{
    /// <summary>
    /// Initializes new receiving-link options with the defaults.
    /// </summary>
    /// <remarks>
    /// A receiver accepts messages of up to 16 MiB (16,777,216 bytes) by default: set
    /// <see cref="AmqpLinkOptions.MaxMessageSize"/> to change the limit, or to <see langword="null"/> to remove it.
    /// The receiver reassembles a delivery before handing it to the application, so without a limit one peer
    /// could make it buffer without end.
    /// </remarks>
    public AmqpReceiverOptions()
    {
        MaxMessageSize = 16ul * 1024 * 1024;
    }

    /// <summary>
    /// Gets or sets how many deliveries the receiver keeps granted or buffered. Defaults to 100.
    /// </summary>
    /// <remarks>
    /// With a positive value, the receiver grants that much credit when the link attaches and tops it up
    /// as the application takes deliveries, so at most that many deliveries wait in its buffer. With zero,
    /// the receiver grants nothing on its own and the application grants credit with
    /// <see cref="IAmqpReceiver.AddCredit(uint)"/>.
    /// </remarks>
    public uint Prefetch { get; set; } = 100;
}
