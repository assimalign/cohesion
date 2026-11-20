using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The receiving endpoint of a link: it grants credit, reassembles deliveries from their <c>transfer</c>
/// frames, buffers them for the application, and sends the application's settlement (AMQP 1.0 Part 2,
/// sections 2.6.7, 2.6.11 and 2.6.12).
/// </summary>
/// <remarks>
/// With a positive prefetch, the receiver keeps that many deliveries granted or buffered, topping credit
/// up once half of it has been taken, so it sends one <c>flow</c> for many deliveries.
/// </remarks>
internal sealed class AmqpReceiverEndpoint : AmqpLinkEndpoint, IAmqpReceiver
{
    private static readonly uint _defaultPrefetch = new AmqpReceiverOptions().Prefetch;

    private readonly Channel<IAmqpDelivery> _deliveries;
    private readonly AmqpWaiter _drained;
    private uint _prefetch;
    private uint _deliveryCount;
    private bool _deliveryCountKnown;
    private uint _linkCredit;
    private int _buffered;
    private bool _draining;
    private AmqpIncomingDelivery? _partial;
    private IReadOnlyDictionary<AmqpSymbol, object?>? _flowProperties;

    /// <summary>
    /// Creates the endpoint of a receiving link the application attaches.
    /// </summary>
    public AmqpReceiverEndpoint(AmqpSessionEndpoint session, uint handle, string name, AmqpReceiverOptions options)
        : base(session, handle, name, AmqpRole.Receiver, options)
    {
        _deliveries = CreateBuffer();
        _drained = new AmqpWaiter();
        _prefetch = options.Prefetch;
    }

    /// <summary>
    /// Creates the endpoint of a link the peer attaches as a sender, which the application has yet to answer.
    /// </summary>
    public AmqpReceiverEndpoint(AmqpSessionEndpoint session, uint handle, AmqpAttachPerformative attach)
        : base(session, handle, AmqpRole.Receiver, attach)
    {
        _deliveries = CreateBuffer();
        _drained = new AmqpWaiter();
        _prefetch = _defaultPrefetch;

        // A sender's attach carries its initial delivery-count (AMQP 1.0 Part 2, section 2.7.3).
        _deliveryCount = attach.InitialDeliveryCount ?? 0;
        _deliveryCountKnown = true;
    }

    public uint Credit => Volatile.Read(ref _linkCredit);

    private protected override uint? InitialDeliveryCount => null;

    public ValueTask<IAmqpDelivery> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_deliveries.Reader.TryRead(out IAmqpDelivery? delivery))
        {
            OnTaken();
            return ValueTask.FromResult(delivery);
        }

        return ReceiveSlowAsync(cancellationToken);
    }

    public bool TryReceive([NotNullWhen(true)] out IAmqpDelivery? delivery)
    {
        if (!_deliveries.Reader.TryRead(out delivery))
        {
            return false;
        }

        OnTaken();
        return true;
    }

    public void AddCredit(uint credit)
    {
        lock (Sync)
        {
            ThrowIfNotAttachedLocked();

            if (credit == 0)
            {
                return;
            }

            _linkCredit = (uint)Math.Min((ulong)_linkCredit + credit, uint.MaxValue);
            SessionEndpoint.WriteFlowLocked(this);
            SessionEndpoint.Writer.Schedule();
        }
    }

    public AmqpDeliveryState? PartialDeliveryState
    {
        get
        {
            lock (Sync)
            {
                // An ignored resume is not the application's to see.
                return _partial is { IsIgnored: false } partial ? partial.RemoteState : null;
            }
        }
    }

    public void SetAcquisitionTransaction(ReadOnlyMemory<byte>? transactionId)
    {
        lock (Sync)
        {
            ThrowIfNotAttachedLocked();

            // The control link carries no transactional work (AMQP 1.0 Part 4, section 4.1).
            if (IsControlLink)
            {
                throw new InvalidOperationException(ErrorMessages.CoordinatorLinkAcquisition);
            }

            // The txn-id entry of the flow's properties names the transaction (AMQP 1.0 Part 4, section 4.4.3);
            // every later flow repeats it, so the sender cannot miss it.
            _flowProperties = transactionId is ReadOnlyMemory<byte> id
                ? new Dictionary<AmqpSymbol, object?>(1) { [TransactionIdKey] = id.ToArray() }
                : null;
            SessionEndpoint.WriteFlowLocked(this);
            SessionEndpoint.Writer.Schedule();
        }
    }

    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;

            lock (Sync)
            {
                ThrowIfNotAttachedLocked();

                if (_linkCredit == 0)
                {
                    return;
                }

                if (!_draining)
                {
                    // Ask the sender to use its credit or give it back (AMQP 1.0 Part 2, section 2.6.7).
                    _draining = true;
                    SessionEndpoint.WriteFlowLocked(this);
                    SessionEndpoint.Writer.Schedule();
                }

                wait = _drained.WaitAsync();
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accepts a link the peer attached as a sender, answers its <c>attach</c>, and grants the prefetch credit.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the options make an <c>attach</c> that breaks a rule spanning its fields, or carry a value the encoder refuses; the request stays unanswered.</exception>
    /// <exception cref="AmqpException">Thrown when the encoded <c>attach</c> exceeds the peer's maximum frame size; the request stays unanswered.</exception>
    internal void AcceptLocked(AmqpReceiverOptions? options)
    {
        AnswerLocked(options);
    }

    /// <summary>
    /// Settles a delivery the application took from the link.
    /// </summary>
    internal ValueTask SettleAsync(AmqpIncomingDelivery delivery, AmqpDeliveryState? state, CancellationToken cancellationToken)
    {
        // The control link carries no transactional work (AMQP 1.0 Part 4, section 4.1).
        if (state is AmqpTransactionalState && IsControlLink)
        {
            throw new ArgumentException(ErrorMessages.CoordinatorOutcomeTransactional, nameof(state));
        }

        Task settlement;

        lock (Sync)
        {
            // Only the first settlement call has an effect, and a settled delivery needs no outcome.
            if (delivery.IsSettling || delivery.IsSettled)
            {
                return ValueTask.CompletedTask;
            }

            // A link or delivery the peer ended is not a usage error, so the failure is the returned task's.
            if (delivery.Failure is AmqpException failure)
            {
                return ValueTask.FromException(failure);
            }

            if (State != AmqpLinkState.Attached)
            {
                return ValueTask.FromException(GetDetachedExceptionLocked());
            }

            AmqpSessionEndpoint session = SessionEndpoint;

            if (delivery.SettlesFirst)
            {
                session.WriteDispositionLocked(AmqpRole.Receiver, delivery.DeliveryId, delivery.DeliveryId, true, state);
                session.RemoveIncomingLocked(delivery.DeliveryId);
                delivery.SettleLocked();
                session.Writer.Schedule();
                return ValueTask.CompletedTask;
            }

            // Receiver settlement mode second: send the outcome unsettled and settle once the sender has
            // (AMQP 1.0 Part 2, section 2.6.12).
            session.WriteDispositionLocked(AmqpRole.Receiver, delivery.DeliveryId, delivery.DeliveryId, false, state);
            settlement = delivery.BeginSettleLocked();
            session.Writer.Schedule();
        }

        return new ValueTask(settlement.WaitAsync(cancellationToken));
    }

    internal override void FillFlowLocked(AmqpFlowPerformative flow)
    {
        // Before the sender's attach arrives, the receiver does not know its delivery-count (section 2.7.4).
        flow.Handle = Handle;
        flow.DeliveryCount = _deliveryCountKnown ? _deliveryCount : null;
        flow.LinkCredit = _linkCredit;
        flow.Drain = _draining;
        flow.Properties = _flowProperties;
    }

    internal override void OnFlowLocked(AmqpFlowPerformative flow)
    {
        if (State != AmqpLinkState.Attached)
        {
            return;
        }

        // A sender that advances its delivery-count without transfers gives back that much credit, which is
        // how it completes a drain (AMQP 1.0 Part 2, section 2.6.7).
        if (flow.DeliveryCount is uint senderCount)
        {
            int advance = AmqpSerialNumber.Difference(senderCount, _deliveryCount);

            if (advance > 0)
            {
                _deliveryCount = senderCount;
                _linkCredit = (uint)advance >= _linkCredit ? 0 : _linkCredit - (uint)advance;
            }
        }

        CheckDrainedLocked();

        if (flow.Echo)
        {
            SessionEndpoint.WriteFlowLocked(this);
        }
    }

    internal override void OnTransferLocked(AmqpTransferPerformative transfer, ReadOnlySpan<byte> payload)
    {
        // Transfers in flight after this endpoint detached go with the link. Before the application answers
        // an attach no credit is granted, so a transfer then exceeds it.
        if (State is not (AmqpLinkState.Attached or AmqpLinkState.RemoteAttached))
        {
            return;
        }

        AmqpSessionEndpoint session = SessionEndpoint;
        AmqpIncomingDelivery? delivery = _partial;

        if (delivery is null)
        {
            // The first frame of a delivery names it and takes a unit of credit (section 2.7.5).
            if (transfer.DeliveryId is not uint deliveryId || transfer.DeliveryTag is not AmqpDeliveryTag deliveryTag)
            {
                FailLocked(AmqpErrorCondition.InvalidField, ErrorMessages.TransferDeliveryIdMissing);
                return;
            }

            if (_linkCredit == 0)
            {
                FailLocked(AmqpErrorCondition.TransferLimitExceeded, ErrorMessages.TransferCreditExhausted);
                return;
            }

            if (transfer.ReceiverSettleMode == AmqpReceiverSettleMode.Second && ReceiverSettleMode == AmqpReceiverSettleMode.First)
            {
                FailLocked(AmqpErrorCondition.InvalidField, ErrorMessages.TransferSettleModeInvalid);
                return;
            }

            if (FailIfControlTransferInvalidLocked(transfer))
            {
                return;
            }

            if (session.ContainsIncomingLocked(deliveryId))
            {
                session.EndWithErrorLocked(
                    AmqpErrorCondition.IllegalState,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryIdDuplicate, deliveryId));
                return;
            }

            if (FailIfUnsettledRuleBrokenLocked(transfer, deliveryTag, out bool resumed))
            {
                return;
            }

            // An ignored resume still takes credit: the sender counted it like any delivery (section 2.6.7).
            _linkCredit--;
            _deliveryCount = unchecked(_deliveryCount + 1);

            delivery = new AmqpIncomingDelivery(
                this,
                deliveryId,
                deliveryTag,
                transfer.MessageFormat ?? 0,
                transfer.Settled == true,
                transfer.State,
                resumed,
                transfer.Resume && !resumed,
                ReceiverSettleMode == AmqpReceiverSettleMode.First || transfer.ReceiverSettleMode == AmqpReceiverSettleMode.First);

            // An ignored delivery is neither tracked nor settled: the receiver acts as if it never arrived.
            if (!delivery.IsSettled && !delivery.IsIgnored)
            {
                session.AddIncomingLocked(delivery);
            }

            _partial = delivery;
        }
        else
        {
            if (transfer.DeliveryId is uint deliveryId && deliveryId != delivery.DeliveryId)
            {
                FailLocked(
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransferDeliveryIdMismatch, delivery.DeliveryId, deliveryId));
                return;
            }

            // A continuation may repeat the delivery's tag and message format, and MUST NOT change either
            // (AMQP 1.0 Part 2, section 2.7.5).
            if (transfer.DeliveryTag is AmqpDeliveryTag deliveryTag && deliveryTag != delivery.DeliveryTag)
            {
                FailLocked(
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransferDeliveryTagMismatch, delivery.DeliveryTag, deliveryTag));
                return;
            }

            if (transfer.MessageFormat is uint messageFormat && messageFormat != delivery.MessageFormat)
            {
                FailLocked(
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransferMessageFormatMismatch, delivery.DeliveryId, delivery.MessageFormat, messageFormat));
                return;
            }

            if (FailIfControlTransferInvalidLocked(transfer))
            {
                return;
            }

            // Only the first frame of a resumed delivery carries the received state (AMQP 1.0 Part 3,
            // section 3.4.1).
            if (transfer.State is AmqpReceived)
            {
                FailLocked(
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransferReceivedNotFirst, delivery.DeliveryId));
                return;
            }

            // Every frame of a delivery belongs to the transaction its first frame named, or to none (AMQP 1.0
            // Part 4, section 4.4.1). A later frame without a state changes nothing.
            if (transfer.State is AmqpDeliveryState state && !delivery.KeepsTransaction(state))
            {
                FailLocked(
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryTransactionChanged, delivery.DeliveryId));
                return;
            }

            // A terminal outcome the sender stated stands: no later frame can alter it (AMQP 1.0 Part 2, section
            // 2.7.5).
            if (transfer.State is AmqpDeliveryState stated && !delivery.KeepsOutcome(stated))
            {
                FailLocked(
                    AmqpErrorCondition.IllegalState,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryOutcomeChanged, delivery.DeliveryId));
                return;
            }

            // A later frame may settle the delivery or update its state.
            if (transfer.Settled == true && !delivery.IsSettled)
            {
                session.RemoveIncomingLocked(delivery.DeliveryId);
            }

            delivery.OnContinuationLocked(transfer.Settled == true, transfer.State);
        }

        if (transfer.Aborted)
        {
            // An aborted delivery is discarded and settled implicitly (AMQP 1.0 Part 2, section 2.6.14). A resumed
            // one still reaches the application, which discards what it kept of it (Part 3, section 3.4.6).
            _partial = null;
            session.RemoveIncomingLocked(delivery.DeliveryId);

            if (delivery.IsResumed)
            {
                delivery.AbortLocked();
                _buffered++;
                _deliveries.Writer.TryWrite(delivery);
            }
            else
            {
                delivery.DiscardLocked();
            }

            CheckDrainedLocked();
            TopUpLocked();
            return;
        }

        if (!delivery.AppendLocked(payload, transfer.More, LocalMaxMessageSize))
        {
            FailLocked(
                AmqpErrorCondition.MessageSizeExceeded,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.IncomingMessageTooLarge, LocalMaxMessageSize));
            return;
        }

        if (transfer.More)
        {
            return;
        }

        _partial = null;

        if (delivery.IsIgnored)
        {
            // Nothing reaches the application, so the credit the ignored delivery took is topped up here.
            CheckDrainedLocked();
            TopUpLocked();
            return;
        }

        delivery.CompleteLocked();
        _buffered++;
        _deliveries.Writer.TryWrite(delivery);
        CheckDrainedLocked();
    }

    /// <summary>
    /// Applies the rules the unsettled maps of the link's attach frames set for the first frame of a delivery
    /// (AMQP 1.0 Part 2, section 2.6.13, and Part 3, section 3.4.1), and detaches the link when the sender
    /// breaks one.
    /// </summary>
    /// <param name="transfer">The delivery's first frame.</param>
    /// <param name="deliveryTag">The delivery's tag.</param>
    /// <param name="resumed">
    /// <see langword="true"/> when the frame resumes a delivery the receiver waits for; a resume of any other
    /// delivery is ignored.
    /// </param>
    /// <returns><see langword="true"/> when the link was detached.</returns>
    private bool FailIfUnsettledRuleBrokenLocked(AmqpTransferPerformative transfer, AmqpDeliveryTag deliveryTag, out bool resumed)
    {
        if (transfer.Resume)
        {
            // The receiver MUST ignore a resumed delivery it does not hold, and holds each one until a transfer
            // resumes or aborts it, once.
            resumed = TakeResumableLocked(deliveryTag);
            return false;
        }

        resumed = false;

        if (transfer.State is AmqpReceived)
        {
            FailLocked(AmqpErrorCondition.InvalidField, ErrorMessages.TransferReceivedNotResumed);
            return true;
        }

        // Only once both maps are complete can a new delivery create unsettled state; a receiver that sent an
        // incomplete map MUST detach on one.
        if (UnsettledIncomplete)
        {
            FailLocked(AmqpErrorCondition.IllegalState, ErrorMessages.TransferNewWhileUnsettledIncomplete);
            return true;
        }

        // A delivery both maps list MUST be resumed, and its tag stays taken until it is.
        if (IsResumableLocked(deliveryTag))
        {
            FailLocked(
                AmqpErrorCondition.IllegalState,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransferTagAwaitsResume, deliveryTag));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detaches a link to a transaction coordinator that the peer sends a settled or transactional frame
    /// on: declare and discharge need the coordinator's outcome, so the coordinator detaches with
    /// <c>amqp:illegal-state</c> as AMQP 1.0 Part 4, section 4.2 asks, and the control link carries no
    /// transactional work (section 4.1).
    /// </summary>
    /// <returns><see langword="true"/> when the link was detached.</returns>
    private bool FailIfControlTransferInvalidLocked(AmqpTransferPerformative transfer)
    {
        if (!IsControlLink)
        {
            return false;
        }

        if (transfer.Settled == true)
        {
            FailLocked(AmqpErrorCondition.IllegalState, ErrorMessages.CoordinatorTransferSettled);
            return true;
        }

        if (transfer.State is AmqpTransactionalState)
        {
            FailLocked(AmqpErrorCondition.NotAllowed, ErrorMessages.CoordinatorTransferTransactional);
            return true;
        }

        return false;
    }

    private protected override void OnAttachSentLocked()
    {
        // Grant the prefetch with the attach rather than a round trip later; until the sender's attach
        // arrives the flow carries no delivery-count (AMQP 1.0 Part 2, section 2.6.7).
        GrantPrefetchLocked();
    }

    private protected override void OnRemoteAttachLocked(AmqpAttachPerformative attach)
    {
        _deliveryCount = attach.InitialDeliveryCount ?? 0;
        _deliveryCountKnown = true;
    }

    private protected override void OnAcceptedLocked(AmqpLinkOptions? options)
    {
        if (options is AmqpReceiverOptions receiver)
        {
            _prefetch = receiver.Prefetch;
        }

        GrantPrefetchLocked();
    }

    private protected override void OnStoppedLocked(AmqpException exception)
    {
        if (_partial is not null)
        {
            _partial.DiscardLocked();
            _partial = null;
        }

        // Deliveries already buffered can still be taken; then receiving fails with the reason.
        _deliveries.Writer.TryComplete(exception);
        _drained.Signal();
    }

    private static Channel<IAmqpDelivery> CreateBuffer()
    {
        return Channel.CreateUnbounded<IAmqpDelivery>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    }

    private async ValueTask<IAmqpDelivery> ReceiveSlowAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            // A receiver that drained, or whose buffer emptied, grants its prefetch again before it waits.
            TopUpLocked();
        }

        try
        {
            IAmqpDelivery delivery = await _deliveries.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            OnTaken();
            return delivery;
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private void OnTaken()
    {
        lock (Sync)
        {
            _buffered--;
            TopUpLocked();
        }
    }

    private void GrantPrefetchLocked()
    {
        _linkCredit = _prefetch;

        if (_linkCredit > 0)
        {
            SessionEndpoint.WriteFlowLocked(this);
        }
    }

    private void TopUpLocked()
    {
        if (State != AmqpLinkState.Attached || _prefetch == 0 || _draining)
        {
            return;
        }

        // Count what is granted, buffered or arriving against the prefetch, and top up once half is gone.
        long held = (long)_linkCredit + _buffered + (_partial is null ? 0 : 1);
        long deficit = _prefetch - held;

        if (deficit <= 0 || deficit < Math.Max(1, _prefetch / 2))
        {
            return;
        }

        _linkCredit += (uint)deficit;
        SessionEndpoint.WriteFlowLocked(this);
        SessionEndpoint.Writer.Schedule();
    }

    private void CheckDrainedLocked()
    {
        if (_draining && _linkCredit == 0)
        {
            _draining = false;
            _drained.Signal();
        }
    }
}
