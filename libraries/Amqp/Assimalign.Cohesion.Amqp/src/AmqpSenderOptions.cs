using System;

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

    /// <summary>
    /// Gets or sets a value indicating whether the sender supports transactional acquisition: a receiver may
    /// name a transaction in the <c>txn-id</c> entry of a <c>flow</c>'s properties, and the deliveries the
    /// sender acquires belong to it (AMQP 1.0 Part 4, section 4.4.3). Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sender that does not support it detaches the link with <see cref="AmqpErrorCondition.NotImplemented"/>
    /// when a <c>flow</c> names a transaction, as section 4.4.3 requires.
    /// </para>
    /// <para>
    /// A sender that does reads the transaction from <see cref="IAmqpSender.AcquisitionTransactionId"/>, stamps
    /// each delivery it acquires under it with an <see cref="AmqpTransactionalState"/> naming it, and calls
    /// <see cref="IAmqpSender.ClearAcquisitionTransaction(ReadOnlyMemory{byte})"/> once the transaction is
    /// discharged.
    /// </para>
    /// </remarks>
    public bool TransactionalAcquisition { get; set; }
}
