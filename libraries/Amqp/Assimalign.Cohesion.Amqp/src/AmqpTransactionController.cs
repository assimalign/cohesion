using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Declares and discharges transactions over a coordinator link (AMQP 1.0 Part 4, section 4.3).
/// </summary>
/// <remarks>
/// <para>
/// Attach the coordinator link as a sender whose target is an <see cref="AmqpCoordinator"/>, then wrap it:
/// </para>
/// <code>
/// IAmqpSender link = await session.AttachSenderAsync(new AmqpSenderOptions { Target = new AmqpCoordinator() });
/// await using AmqpTransactionController controller = AmqpTransactionController.Create(link);
/// ReadOnlyMemory&lt;byte&gt; transaction = await controller.DeclareAsync();
/// </code>
/// <para>
/// Each declare and discharge is an unsettled delivery whose outcome is the coordinator's answer. Disposing
/// the controller closes the link, and the coordinator rolls back every transaction the link declared
/// and did not discharge.
/// </para>
/// </remarks>
public sealed class AmqpTransactionController : IAmqpTransactionController
{
    private AmqpTransactionController(IAmqpSender link)
    {
        Link = link;
    }

    /// <summary>
    /// Gets the coordinator link the controller sends on.
    /// </summary>
    public IAmqpSender Link { get; }

    /// <summary>
    /// Creates a controller over an attached coordinator link.
    /// </summary>
    /// <param name="link">A sending link whose target is an <see cref="AmqpCoordinator"/>. The controller takes ownership of it.</param>
    /// <returns>The controller.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="link"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the link's target is not a coordinator.</exception>
    public static AmqpTransactionController Create(IAmqpSender link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (link.Target is not AmqpCoordinator)
        {
            throw new ArgumentException(ErrorMessages.CoordinatorTargetRequired, nameof(link));
        }

        return new AmqpTransactionController(link);
    }

    /// <summary>
    /// Declares a local transaction.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait for the coordinator's answer.</param>
    /// <returns>The identifier the coordinator allocated.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.TransactionFailed"/> when the coordinator refuses the declare, or with <see cref="AmqpErrorCode.LinkDetached"/> when the coordinator link detaches first.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> DeclareAsync(CancellationToken cancellationToken = default)
    {
        AmqpDeliveryState? outcome = await SendAsync(new AmqpDeclare(), cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            AmqpDeclared declared => declared.TransactionId,
            AmqpRejected rejected => throw new AmqpException(AmqpErrorCode.TransactionFailed, ErrorMessages.TransactionDeclareRefused) { Error = rejected.Error },
            _ => throw new AmqpException(
                AmqpErrorCode.TransactionFailed,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransactionOutcomeUnexpected, outcome?.ToString() ?? "null"))
        };
    }

    /// <summary>
    /// Discharges a transaction: commits its work, or rolls it back.
    /// </summary>
    /// <param name="transactionId">The identifier of the transaction.</param>
    /// <param name="fail"><see langword="true"/> to roll the transaction back; <see langword="false"/> to commit it.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the coordinator's answer.</param>
    /// <returns>A task that completes when the coordinator accepted the discharge.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.TransactionFailed"/> when the coordinator refuses the discharge, such as with <see cref="AmqpErrorCondition.TransactionRollback"/> for a commit it could not complete.</exception>
    public async ValueTask DischargeAsync(ReadOnlyMemory<byte> transactionId, bool fail = false, CancellationToken cancellationToken = default)
    {
        AmqpDeliveryState? outcome = await SendAsync(new AmqpDischarge(transactionId, fail), cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case AmqpAccepted:
                return;
            case AmqpRejected rejected:
                throw new AmqpException(AmqpErrorCode.TransactionFailed, ErrorMessages.TransactionDischargeRefused) { Error = rejected.Error };
            default:
                throw new AmqpException(
                    AmqpErrorCode.TransactionFailed,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransactionOutcomeUnexpected, outcome?.ToString() ?? "null"));
        }
    }

    /// <summary>
    /// Closes the coordinator link. The coordinator rolls back every transaction the link declared and did
    /// not discharge.
    /// </summary>
    /// <returns>A task that completes when the link is closed.</returns>
    public ValueTask DisposeAsync()
    {
        return Link.DisposeAsync();
    }

    private ValueTask<AmqpDeliveryState?> SendAsync(object control, CancellationToken cancellationToken)
    {
        AmqpMessage message = new()
        {
            Body = new AmqpValueBody(control)
        };

        return Link.SendAsync(message, new AmqpDeliveryOptions { Settled = false }, cancellationToken);
    }
}
