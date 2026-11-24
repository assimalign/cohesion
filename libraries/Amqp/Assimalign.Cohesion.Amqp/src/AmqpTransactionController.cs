using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Declares and discharges transactions over a coordinator link (AMQP 1.0 Part 4, section 4.3).
/// </summary>
/// <remarks>
/// <para>
/// Attach the control link with <see cref="AttachAsync(IAmqpSession, IReadOnlyList{AmqpSymbol}?, CancellationToken)"/>,
/// or wrap a sender already attached to an <see cref="AmqpCoordinator"/> with <see cref="Create(IAmqpSender)"/>:
/// </para>
/// <code>
/// await using AmqpTransactionController controller = await AmqpTransactionController.AttachAsync(session);
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
    private static readonly IReadOnlyList<AmqpSymbol> _defaultCapabilities = [AmqpTransactionCapability.LocalTransactions];
    private static readonly IReadOnlyList<AmqpSymbol> _controlOutcomes = [AmqpAccepted.Descriptor, AmqpRejected.Descriptor, AmqpDeclared.Descriptor];

    private AmqpTransactionController(IAmqpSender link)
    {
        Link = link;
    }

    /// <summary>
    /// Gets the coordinator link the controller sends on.
    /// </summary>
    public IAmqpSender Link { get; }

    /// <summary>
    /// Gets the capabilities the coordinator announced in its <c>attach</c>, or <see langword="null"/> when it
    /// announced none (AMQP 1.0 Part 4, section 4.5.1).
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities => (Link.Target as AmqpCoordinator)?.Capabilities;

    /// <summary>
    /// Attaches a control link to the session's peer and creates a controller over it.
    /// </summary>
    /// <param name="session">The session the transactional work happens on.</param>
    /// <param name="capabilities">
    /// The capabilities the controller needs, which its <c>attach</c> asks for (section 4.5.1).
    /// <see langword="null"/> asks for <see cref="AmqpTransactionCapability.LocalTransactions"/> alone.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the attach.</param>
    /// <returns>The controller.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="AmqpException">
    /// Thrown with <see cref="AmqpErrorCode.TransactionFailed"/> when the coordinator announces capabilities
    /// that leave out one the controller asked for, or answers without a coordinator target, after the link is
    /// closed; or with <see cref="AmqpErrorCode.LinkDetached"/> when the peer refuses the link.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The link sends unsettled, as every declare and discharge must be (sections 4.2 and 4.3), and its source
    /// supports the <see cref="AmqpRejected"/> outcome, so the coordinator refuses a control message by
    /// rejecting it rather than by detaching the link, as section 4.2 asks a controller to allow.
    /// </para>
    /// <para>
    /// A coordinator that announces no capabilities is taken to support the ones asked for: AMQP 1.0 leaves
    /// that case open, and refusing it would refuse coordinators that support local transactions without
    /// saying so.
    /// </para>
    /// </remarks>
    public static async ValueTask<AmqpTransactionController> AttachAsync(
        IAmqpSession session,
        IReadOnlyList<AmqpSymbol>? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        IReadOnlyList<AmqpSymbol> desired = capabilities ?? _defaultCapabilities;
        IAmqpSender link = await session.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Source = new AmqpSource
                {
                    Outcomes = _controlOutcomes
                },
                Target = new AmqpCoordinator
                {
                    Capabilities = desired
                },
                SenderSettleMode = AmqpSenderSettleMode.Unsettled
            },
            cancellationToken).ConfigureAwait(false);

        string? refusal = link.Target is AmqpCoordinator coordinator
            ? FindMissingCapability(coordinator.Capabilities, desired)
            : ErrorMessages.CoordinatorTargetRequired;

        if (refusal is not null)
        {
            await link.CloseAsync(new AmqpError(AmqpErrorCondition.NotImplemented, refusal), CancellationToken.None).ConfigureAwait(false);

            throw new AmqpException(AmqpErrorCode.TransactionFailed, refusal);
        }

        return new AmqpTransactionController(link);
    }

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

    private static string? FindMissingCapability(IReadOnlyList<AmqpSymbol>? supported, IReadOnlyList<AmqpSymbol> desired)
    {
        if (supported is null)
        {
            return null;
        }

        foreach (AmqpSymbol capability in desired)
        {
            bool found = false;

            foreach (AmqpSymbol candidate in supported)
            {
                if (candidate == capability)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return string.Format(CultureInfo.CurrentCulture, ErrorMessages.CoordinatorCapabilityMissing, capability);
            }
        }

        return null;
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
