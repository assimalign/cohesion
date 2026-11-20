namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Configures a sending link: the settings
/// <see cref="IAmqpSession.AttachSenderAsync(AmqpSenderOptions, System.Threading.CancellationToken)"/> attaches
/// with, or <see cref="IAmqpLinkRequest.AcceptSenderAsync(AmqpSenderOptions?, System.Threading.CancellationToken)"/>
/// answers with.
/// </summary>
public sealed class AmqpSenderOptions : AmqpLinkOptions
{
    /// <summary>
    /// Initializes new sending-link options with the defaults.
    /// </summary>
    public AmqpSenderOptions()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether the sender completes a drain the receiver asks for on its
    /// own: once no send is waiting for credit, it gives back the credit it has not used. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// With the value <see langword="false"/>, the application watches <see cref="IAmqpSender.IsDraining"/>,
    /// sends what it has, and then calls <see cref="IAmqpSender.CompleteDrain"/>.
    /// </remarks>
    public bool AutoCompleteDrain { get; set; } = true;
}
