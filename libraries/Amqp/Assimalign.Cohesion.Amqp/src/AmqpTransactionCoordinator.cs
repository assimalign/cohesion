using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Runs a transaction coordinator on a control link: it decodes each declare and discharge the controller
/// sends, hands it to an <see cref="IAmqpTransactionHandler"/>, and answers with the outcome (AMQP 1.0 Part 4,
/// sections 4.2 and 4.3).
/// </summary>
/// <remarks>
/// <para>
/// Accept a controller's link with <see cref="AcceptAsync(IAmqpLinkRequest, IAmqpTransactionHandler, IReadOnlyList{AmqpSymbol}?, CancellationToken)"/>,
/// or wrap one already accepted with <see cref="Create(IAmqpReceiver, IAmqpTransactionHandler)"/>, then run it:
/// </para>
/// <code>
/// IAmqpLinkRequest request = await session.AcceptLinkAsync();
///
/// if (request.Target is AmqpCoordinator)
/// {
///     await using AmqpTransactionCoordinator coordinator = await AmqpTransactionCoordinator.AcceptAsync(request, handler);
///     await coordinator.RunAsync();
/// }
/// </code>
/// <para>
/// A declare is answered with <see cref="AmqpDeclared"/> and a discharge with <see cref="AmqpAccepted"/>.
/// A control message the coordinator cannot perform is refused with a <see cref="AmqpRejected"/> outcome
/// carrying the error when the controller's source supports that outcome, and otherwise by closing the
/// control link with the error, as sections 4.2 and 4.3 require. A message that is not a declare or a
/// discharge is refused with <see cref="AmqpErrorCondition.NotImplemented"/>, one that does not decode with
/// <see cref="AmqpErrorCondition.DecodeError"/>, and a discharge of a transaction the link did not declare,
/// or already discharged, with <see cref="AmqpErrorCondition.TransactionUnknownId"/>.
/// </para>
/// <para>
/// The engine already holds the control link to the rest of Part 4: it detaches it with
/// <see cref="AmqpErrorCondition.IllegalState"/> on a settled declare or discharge, and with
/// <see cref="AmqpErrorCondition.NotAllowed"/> on transactional work.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpTransactionCoordinator {Link.Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpTransactionCoordinator : IAmqpTransactionCoordinator
{
    private const int maxTransactionIdLength = 32;

    private static readonly IReadOnlyList<AmqpSymbol> _defaultCapabilities = [AmqpTransactionCapability.LocalTransactions];

    private readonly IAmqpTransactionHandler _handler;
    private readonly Lock _sync;
    private readonly List<byte[]> _active;
    private int _started;

    private AmqpTransactionCoordinator(IAmqpReceiver link, IAmqpTransactionHandler handler)
    {
        Link = link;
        _handler = handler;
        _sync = new Lock();
        _active = [];
    }

    /// <summary>
    /// Gets the control link the coordinator receives on.
    /// </summary>
    public IAmqpReceiver Link { get; }

    /// <summary>
    /// Creates a coordinator over a control link the application already accepted.
    /// </summary>
    /// <param name="link">A receiving link whose target is an <see cref="AmqpCoordinator"/>. The coordinator takes ownership of it.</param>
    /// <param name="handler">The transactional resource that performs each declare and discharge.</param>
    /// <returns>The coordinator, which starts receiving when <see cref="RunAsync(CancellationToken)"/> is called.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="link"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the link's target is not a coordinator.</exception>
    public static AmqpTransactionCoordinator Create(IAmqpReceiver link, IAmqpTransactionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(handler);

        if (link.Target is not AmqpCoordinator)
        {
            throw new ArgumentException(ErrorMessages.CoordinatorTargetRequired, nameof(link));
        }

        return new AmqpTransactionCoordinator(link, handler);
    }

    /// <summary>
    /// Accepts a controller's request for a control link and creates a coordinator over it.
    /// </summary>
    /// <param name="request">A request whose peer attaches as a sender with an <see cref="AmqpCoordinator"/> target.</param>
    /// <param name="handler">The transactional resource that performs each declare and discharge.</param>
    /// <param name="capabilities">
    /// The capabilities the coordinator supports, which its <c>attach</c> announces (section 4.5.1); the
    /// controller decides whether they meet its needs. <see langword="null"/> announces
    /// <see cref="AmqpTransactionCapability.LocalTransactions"/> alone.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The coordinator, which starts receiving when <see cref="RunAsync(CancellationToken)"/> is called.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the request is not a sender's link to a coordinator. The request stays unanswered.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.SessionEnded"/> when the session has ended.</exception>
    /// <remarks>
    /// A link to the coordinator cannot be resumed (section 4.2), so the answer resumes nothing the request
    /// lists as unsettled.
    /// </remarks>
    public static async ValueTask<AmqpTransactionCoordinator> AcceptAsync(
        IAmqpLinkRequest request,
        IAmqpTransactionHandler handler,
        IReadOnlyList<AmqpSymbol>? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);

        if (request.Role != AmqpRole.Receiver || request.Target is not AmqpCoordinator)
        {
            throw new ArgumentException(ErrorMessages.CoordinatorRequestRequired, nameof(request));
        }

        IAmqpReceiver link = await request.AcceptReceiverAsync(
            new AmqpReceiverOptions
            {
                Target = new AmqpCoordinator
                {
                    Capabilities = capabilities ?? _defaultCapabilities
                }
            },
            cancellationToken).ConfigureAwait(false);

        return new AmqpTransactionCoordinator(link, handler);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ReadOnlyMemory<byte>> GetActiveTransactions()
    {
        lock (_sync)
        {
            ReadOnlyMemory<byte>[] snapshot = new ReadOnlyMemory<byte>[_active.Count];

            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = _active[i];
            }

            return snapshot;
        }
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.CoordinatorAlreadyRunning);
        }

        ExceptionDispatchInfo? failure = null;
        AmqpError? closeError = null;

        try
        {
            while (await ReceiveAsync(cancellationToken).ConfigureAwait(false) is IAmqpDelivery delivery)
            {
                if (!await HandleAsync(delivery, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        catch (Exception exception)
        {
            // Justified catch-all: whatever the handler throws, the control link closes and its live
            // transactions roll back before the exception surfaces.
            closeError = new AmqpError(AmqpErrorCondition.InternalError, ErrorMessages.CoordinatorHandlerFailed);
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        // A control link that ends rolls back every transaction it declared and did not discharge (section 4.1).
        await CloseLinkAsync(closeError).ConfigureAwait(false);

        ExceptionDispatchInfo? rollbackFailure = await RollBackAsync().ConfigureAwait(false);

        (failure ?? rollbackFailure)?.Throw();
    }

    /// <summary>
    /// Closes the control link. A running coordinator then rolls back every transaction the link declared
    /// and did not discharge, and its run completes.
    /// </summary>
    /// <returns>A task that completes when the link is closed.</returns>
    public ValueTask DisposeAsync()
    {
        return Link.DisposeAsync();
    }

    private static bool SupportsRejected(AmqpSource? source)
    {
        if (source?.Outcomes is not { } outcomes)
        {
            return false;
        }

        foreach (AmqpSymbol outcome in outcomes)
        {
            if (outcome == AmqpRejected.Descriptor)
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<IAmqpDelivery?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Link.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AmqpException exception) when (IsLinkEnded(exception))
        {
            // The control link ended: the run is over.
            return null;
        }
    }

    /// <returns><see langword="false"/> when the control link ended.</returns>
    private async ValueTask<bool> HandleAsync(IAmqpDelivery delivery, CancellationToken cancellationToken)
    {
        object? control;

        try
        {
            // A control message is a declare or a discharge in an amqp-value body; its other sections are
            // ignored (section 4.2).
            control = delivery.GetMessage().Body is AmqpValueBody body ? body.Value : null;
        }
        catch (AmqpException exception)
        {
            return await RefuseAsync(delivery, new AmqpError(AmqpErrorCondition.DecodeError, exception.Message), cancellationToken).ConfigureAwait(false);
        }

        return control switch
        {
            AmqpDeclare declare => await DeclareAsync(delivery, declare, cancellationToken).ConfigureAwait(false),
            AmqpDischarge discharge => await DischargeAsync(delivery, discharge, cancellationToken).ConfigureAwait(false),
            _ => await RefuseAsync(delivery, new AmqpError(AmqpErrorCondition.NotImplemented, ErrorMessages.CoordinatorMessageInvalid), cancellationToken).ConfigureAwait(false)
        };
    }

    private async ValueTask<bool> DeclareAsync(IAmqpDelivery delivery, AmqpDeclare declare, CancellationToken cancellationToken)
    {
        byte[] transactionId;

        try
        {
            transactionId = (await _handler.DeclareAsync(declare, cancellationToken).ConfigureAwait(false)).ToArray();
        }
        catch (AmqpException exception)
        {
            return await RefuseAsync(delivery, GetError(exception), cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            // Two live transactions with one identifier could not be told apart: the handler broke its contract.
            if (FindLocked(transactionId) is not null)
            {
                throw new InvalidOperationException(ErrorMessages.TransactionIdDuplicate);
            }

            _active.Add(transactionId);
        }

        // A transaction identifier is at most 32 octets (section 4.5.4). The transaction is live, so the
        // failed run rolls it back with the others.
        if (transactionId.Length > maxTransactionIdLength)
        {
            throw new InvalidOperationException(ErrorMessages.TransactionIdTooLong);
        }

        return await SettleAsync(delivery, new AmqpDeclared(transactionId), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> DischargeAsync(IAmqpDelivery delivery, AmqpDischarge discharge, CancellationToken cancellationToken)
    {
        byte[]? transactionId;

        lock (_sync)
        {
            transactionId = FindLocked(discharge.TransactionId.Span);
        }

        // The link discharges only what it declared: another link's transaction rolls back with that link.
        if (transactionId is null)
        {
            return await RefuseAsync(
                delivery,
                new AmqpError(AmqpErrorCondition.TransactionUnknownId, ErrorMessages.TransactionUnknown),
                cancellationToken).ConfigureAwait(false);
        }

        AmqpError? refusal = null;

        try
        {
            await _handler.DischargeAsync(transactionId, discharge.Fail, cancellationToken).ConfigureAwait(false);
        }
        catch (AmqpException exception)
        {
            refusal = GetError(exception);
        }

        // A refused discharge ends the transaction too: each transaction error says it is gone (section 4.5.8).
        // A handler that threw anything else left it live, so the failed run rolls it back.
        lock (_sync)
        {
            _active.Remove(transactionId);
        }

        return refusal is null
            ? await SettleAsync(delivery, AmqpAccepted.Instance, cancellationToken).ConfigureAwait(false)
            : await RefuseAsync(delivery, refusal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a control message: rejects it when the controller's source supports the rejected outcome, and
    /// otherwise closes the control link with the error (sections 4.2 and 4.3).
    /// </summary>
    /// <returns><see langword="false"/> when the control link ended.</returns>
    private async ValueTask<bool> RefuseAsync(IAmqpDelivery delivery, AmqpError error, CancellationToken cancellationToken)
    {
        if (SupportsRejected(Link.Source))
        {
            return await SettleAsync(delivery, new AmqpRejected(error), cancellationToken).ConfigureAwait(false);
        }

        await CloseLinkAsync(error).ConfigureAwait(false);
        return false;
    }

    /// <returns><see langword="false"/> when the control link ended before the outcome went out.</returns>
    private async ValueTask<bool> SettleAsync(IAmqpDelivery delivery, AmqpDeliveryState outcome, CancellationToken cancellationToken)
    {
        try
        {
            await delivery.SettleAsync(outcome, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmqpException exception) when (IsLinkEnded(exception))
        {
            return false;
        }
    }

    private async ValueTask CloseLinkAsync(AmqpError? error)
    {
        if (!Link.LinkDetached.IsCancellationRequested)
        {
            await Link.CloseAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls back every transaction the control link declared and did not discharge.
    /// </summary>
    /// <returns>The first failure of a rollback, once every rollback was tried; otherwise <see langword="null"/>.</returns>
    private async ValueTask<ExceptionDispatchInfo?> RollBackAsync()
    {
        ExceptionDispatchInfo? failure = null;

        while (true)
        {
            byte[] transactionId;

            lock (_sync)
            {
                if (_active.Count == 0)
                {
                    return failure;
                }

                transactionId = _active[0];
                _active.RemoveAt(0);
            }

            try
            {
                await _handler.DischargeAsync(transactionId, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Justified catch-all: one failed rollback must not leave the link's other transactions live.
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    /// <summary>
    /// Decides from the exception, not from <see cref="IAmqpLink.LinkDetached"/>, whether an operation failed
    /// because the control link ended: the exception can reach the run before the token is canceled.
    /// </summary>
    private static bool IsLinkEnded(AmqpException exception)
    {
        return exception.ErrorCode is AmqpErrorCode.LinkDetached or AmqpErrorCode.SessionEnded or AmqpErrorCode.ConnectionClosed;
    }

    private static AmqpError GetError(AmqpException exception)
    {
        return exception.Error ?? new AmqpError(AmqpErrorCondition.InternalError, exception.Message);
    }

    private byte[]? FindLocked(ReadOnlySpan<byte> transactionId)
    {
        foreach (byte[] candidate in _active)
        {
            if (candidate.AsSpan().SequenceEqual(transactionId))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class DebugView
    {
        private readonly AmqpTransactionCoordinator _coordinator;

        public DebugView(AmqpTransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public IAmqpReceiver Link => _coordinator.Link;
    }
}
