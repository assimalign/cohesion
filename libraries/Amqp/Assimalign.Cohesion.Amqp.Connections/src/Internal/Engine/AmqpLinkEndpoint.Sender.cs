using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The sending endpoint of a link: it sends deliveries within the credit the receiver grants and the peer's
/// incoming window, splitting each into as many <c>transfer</c> frames as the frame size requires (AMQP 1.0
/// Part 2, sections 2.6.7 and 2.6.11).
/// </summary>
/// <remarks>
/// Sends are serialized by a semaphore, so the frames of one delivery are contiguous on the link. A send
/// takes the lock only to encode frames; it waits for credit, window or carrier capacity outside it, and
/// releases the semaphore once its last frame is queued, so it awaits the receiver's outcome without
/// holding up the next send.
/// </remarks>
internal sealed class AmqpSenderEndpoint : AmqpLinkEndpoint, IAmqpSender
{
    private readonly SemaphoreSlim _sendLock;
    private readonly AmqpWaiter _credit;
    private readonly AmqpTransferPerformative _transfer;
    private bool _autoCompleteDrain;
    private bool _transactionalAcquisition;
    private ReadOnlyMemory<byte>? _acquisitionTransactionId;
    private uint _deliveryCount;
    private uint _linkCredit;
    private bool _drain;
    private int _pendingSends;
    private ulong _nextTag;

    /// <summary>
    /// Creates the endpoint of a sending link the application attaches.
    /// </summary>
    public AmqpSenderEndpoint(AmqpSessionEndpoint session, uint handle, string name, AmqpSenderOptions options)
        : base(session, handle, name, AmqpRole.Sender, options)
    {
        _sendLock = new SemaphoreSlim(1, 1);
        _credit = new AmqpWaiter();
        _transfer = new AmqpTransferPerformative();
        _autoCompleteDrain = options.AutoCompleteDrain;
        _transactionalAcquisition = options.TransactionalAcquisition;
    }

    /// <summary>
    /// Creates the endpoint of a link the peer attaches as a receiver, which the application has yet to answer.
    /// </summary>
    public AmqpSenderEndpoint(AmqpSessionEndpoint session, uint handle, AmqpAttachPerformative attach)
        : base(session, handle, AmqpRole.Sender, attach, null)
    {
        _sendLock = new SemaphoreSlim(1, 1);
        _credit = new AmqpWaiter();
        _transfer = new AmqpTransferPerformative();
        _autoCompleteDrain = true;
    }

    public uint Credit => Volatile.Read(ref _linkCredit);

    public bool IsDraining => Volatile.Read(ref _drain);

    public ReadOnlyMemory<byte>? AcquisitionTransactionId
    {
        get
        {
            lock (Sync)
            {
                return _acquisitionTransactionId;
            }
        }
    }

    private protected override uint? InitialDeliveryCount => _deliveryCount;

    public async ValueTask WaitForCreditAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;

            lock (Sync)
            {
                ThrowIfNotAttachedLocked();

                if (_linkCredit > 0)
                {
                    return;
                }

                wait = _credit.WaitAsync();
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void CompleteDrain()
    {
        lock (Sync)
        {
            if (State == AmqpLinkState.Attached && _drain && _linkCredit > 0)
            {
                CompleteDrainLocked();
                SessionEndpoint.Writer.Schedule();
            }
        }
    }

    public bool ClearAcquisitionTransaction(ReadOnlyMemory<byte> transactionId)
    {
        lock (Sync)
        {
            if (_acquisitionTransactionId is not ReadOnlyMemory<byte> current || !current.Span.SequenceEqual(transactionId.Span))
            {
                return false;
            }

            _acquisitionTransactionId = null;
            return true;
        }
    }

    public async ValueTask<AmqpDeliveryState?> SendAsync(AmqpMessage message, AmqpDeliveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        AmqpBufferWriter buffer = new();

        try
        {
            AmqpMessageEncoding.Encode(buffer, message);

            // The buffer is copied into transfer frames before the send awaits the outcome, but it is only
            // returned to the pool once the send completes.
            return await SendAsync(buffer.WrittenMemory, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public async ValueTask<AmqpDeliveryState?> SendAsync(ReadOnlyMemory<byte> payload, AmqpDeliveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        bool settled = ResolveSettled(options);

        if (IsControlLink)
        {
            // The controller must learn the coordinator's outcome (AMQP 1.0 Part 4, sections 4.2 and 4.3), and
            // the control link carries no transactional work (section 4.1).
            if (settled)
            {
                throw new ArgumentException(ErrorMessages.CoordinatorDeliverySettled, nameof(options));
            }

            if (options?.State is AmqpTransactionalState)
            {
                throw new ArgumentException(ErrorMessages.CoordinatorDeliveryTransactional, nameof(options));
            }
        }

        if (options is { Resume: true, DeliveryTag: null })
        {
            throw new ArgumentException(ErrorMessages.DeliveryResumeTagRequired, nameof(options));
        }

        // The received state tells the receiver where a resumed delivery's payload starts, so only the first
        // frame of a resumed delivery carries it (AMQP 1.0 Part 3, section 3.4.1).
        if (options is { State: AmqpReceived, Resume: false })
        {
            throw new ArgumentException(ErrorMessages.DeliveryReceivedNotResumed, nameof(options));
        }

        if (RemoteMaxMessageSize is ulong maxMessageSize && (ulong)payload.Length > maxMessageSize)
        {
            throw new AmqpException(
                AmqpErrorCode.MessageSizeExceeded,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.MessageSizeExceeded, payload.Length, maxMessageSize));
        }

        AmqpOutgoingDelivery? delivery = await SendCoreAsync(payload, options, settled, false, cancellationToken).ConfigureAwait(false);

        // A delivery sent settled is done once its frames are queued; an unsettled one waits for the outcome.
        return delivery is null
            ? null
            : await delivery.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AbortDeliveryAsync(AmqpDeliveryTag deliveryTag, CancellationToken cancellationToken = default)
    {
        // A sender that cannot resume a delivery to completion sends a resuming transfer with the aborted flag
        // (AMQP 1.0 Part 3, section 3.4.6). Under the settled sender settlement mode every delivery is sent
        // settled; under the others an aborted transfer may say either, and is settled implicitly anyway.
        AmqpDeliveryOptions options = new() { DeliveryTag = deliveryTag, Resume = true };

        await SendCoreAsync(ReadOnlyMemory<byte>.Empty, options, SenderSettleMode == AmqpSenderSettleMode.Settled, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts a link the peer attached as a receiver and answers its <c>attach</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the options make an <c>attach</c> that breaks a rule spanning its fields, or carry a value the encoder refuses; the request stays unanswered.</exception>
    /// <exception cref="AmqpException">Thrown when the encoded <c>attach</c> exceeds the peer's maximum frame size; the request stays unanswered.</exception>
    internal void AcceptLocked(AmqpSenderOptions? options)
    {
        AnswerLocked(options);
    }

    internal override void FillFlowLocked(AmqpFlowPerformative flow)
    {
        flow.Handle = Handle;
        flow.DeliveryCount = _deliveryCount;
        flow.LinkCredit = _linkCredit;
        flow.Available = (uint)_pendingSends;
        flow.Drain = _drain;
    }

    internal override void OnFlowLocked(AmqpFlowPerformative flow)
    {
        // A receiver may grant credit before the application answers its attach.
        if (State is not (AmqpLinkState.Attached or AmqpLinkState.RemoteAttached))
        {
            return;
        }

        if (!TakeAcquisitionTransactionLocked(flow))
        {
            return;
        }

        if (flow.LinkCredit is uint credit)
        {
            // The receiver's credit counts from its delivery-count, which lags this endpoint's by the transfers
            // it has not seen yet (AMQP 1.0 Part 2, section 2.6.7). A receiver that has not seen this
            // endpoint's attach counts from the initial delivery-count, zero.
            int lag = AmqpSerialNumber.Difference(_deliveryCount, flow.DeliveryCount ?? 0);
            uint unseen = lag > 0 ? (uint)lag : 0;

            _linkCredit = credit > unseen ? credit - unseen : 0;
        }

        _drain = flow.Drain;

        if (_linkCredit > 0)
        {
            _credit.Signal();
        }

        if (State == AmqpLinkState.Attached && !TryAutoCompleteDrainLocked() && flow.Echo)
        {
            SessionEndpoint.WriteFlowLocked(this);
        }
    }

    internal override void OnTransferLocked(AmqpTransferPerformative transfer, ReadOnlySpan<byte> payload)
    {
        // The peer receives on this link, so it has no transfer to send on it.
        FailLocked(AmqpErrorCondition.IllegalState, ErrorMessages.LinkRoleUnexpected);
    }

    private protected override void OnAcceptedLocked(AmqpLinkOptions? options)
    {
        if (options is AmqpSenderOptions sender)
        {
            _autoCompleteDrain = sender.AutoCompleteDrain;
            _transactionalAcquisition = sender.TransactionalAcquisition;
        }

        // A receiver may have named a transaction before the application answered, when the endpoint could not
        // tell yet whether it supports transactional acquisition (AMQP 1.0 Part 4, section 4.4.3).
        if (_acquisitionTransactionId is not null && !_transactionalAcquisition)
        {
            _acquisitionTransactionId = null;
            FailLocked(AmqpErrorCondition.NotImplemented, ErrorMessages.FlowTransactionUnsupported);
            return;
        }

        // A receiver may have asked for a drain before the application answered.
        TryAutoCompleteDrainLocked();
    }

    private protected override void OnStoppedLocked(AmqpException exception)
    {
        // Wake a send waiting for credit; the session wakes those waiting for its window.
        _credit.Signal();
    }

    /// <summary>
    /// Takes the transaction a <c>flow</c> names in its <c>txn-id</c> property for the deliveries the sender
    /// acquires (AMQP 1.0 Part 4, section 4.4.3). A <c>flow</c> that names none leaves the transaction as it
    /// was: only a discharge clears it.
    /// </summary>
    /// <returns><see langword="false"/> when the link failed.</returns>
    private bool TakeAcquisitionTransactionLocked(AmqpFlowPerformative flow)
    {
        if (flow.Properties is not { } properties || !properties.TryGetValue(TransactionIdKey, out object? value))
        {
            return true;
        }

        // An endpoint that does not support transactional acquisition MUST terminate the link with
        // not-implemented. Until the application answers the attach it cannot tell, so the answer decides.
        if (State == AmqpLinkState.Attached && !_transactionalAcquisition)
        {
            FailLocked(AmqpErrorCondition.NotImplemented, ErrorMessages.FlowTransactionUnsupported);
            return false;
        }

        // A transaction identifier is binary (section 4.5.4).
        if (value is not byte[] transactionId)
        {
            FailLocked(AmqpErrorCondition.InvalidField, ErrorMessages.FlowTransactionIdInvalid);
            return false;
        }

        _acquisitionTransactionId = transactionId;
        return true;
    }

    /// <summary>
    /// Sends one delivery, or the aborting transfer of one carried over from an earlier attachment, once the
    /// sends ahead of it are done.
    /// </summary>
    /// <returns>The delivery, until its outcome settles it; <see langword="null"/> for one sent settled or aborted.</returns>
    private async ValueTask<AmqpOutgoingDelivery?> SendCoreAsync(
        ReadOnlyMemory<byte> payload,
        AmqpDeliveryOptions? options,
        bool settled,
        bool abort,
        CancellationToken cancellationToken)
    {
        lock (Sync)
        {
            ThrowIfNotAttachedLocked();
            ThrowIfUnsettledRuleBrokenLocked(options, abort);
            _pendingSends++;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await TransferAsync(payload, options, settled, abort, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            lock (Sync)
            {
                _pendingSends--;

                if (TryAutoCompleteDrainLocked())
                {
                    SessionEndpoint.Writer.Schedule();
                }
            }
        }
    }

    /// <summary>
    /// Refuses a delivery the unsettled maps of the link's attach frames rule out (AMQP 1.0 Part 2, section
    /// 2.6.13): a resume of a delivery the receiver does not hold or that was already resumed, a new delivery
    /// while either map was incomplete, and a new delivery under the tag of one the receiver waits to have
    /// resumed. A send is checked when it is admitted and again before its first frame, since a send ahead of
    /// it may have resumed the same delivery in between.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the tag breaks a rule.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the delivery is new and either map was incomplete.</exception>
    private void ThrowIfUnsettledRuleBrokenLocked(AmqpDeliveryOptions? options, bool abort)
    {
        if (options is { Resume: true, DeliveryTag: AmqpDeliveryTag resumed })
        {
            // The receiver ignores a resume of a delivery it does not hold, so the delivery would never settle,
            // and a sender MUST NOT resume one it does not hold itself.
            if (!IsResumableLocked(resumed))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryResumeTagUnknown, resumed),
                    abort ? "deliveryTag" : nameof(options));
            }

            return;
        }

        // Only once both maps are complete can a new delivery create unsettled state.
        if (UnsettledIncomplete)
        {
            throw new InvalidOperationException(ErrorMessages.DeliveryNewWhileUnsettledIncomplete);
        }

        // A delivery both maps list MUST be resumed, and its tag stays taken until it is.
        if (options?.DeliveryTag is AmqpDeliveryTag tag && IsResumableLocked(tag))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryTagAwaitsResume, tag),
                nameof(options));
        }
    }

    /// <summary>
    /// Returns the tag the next generated delivery takes, past every tag either unsettled map of the attach
    /// listed.
    /// </summary>
    private AmqpDeliveryTag NextGeneratedTagLocked()
    {
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(_nextTag);

        while (IsCarriedOverLocked(tag))
        {
            _nextTag++;
            tag = AmqpDeliveryTag.Create(_nextTag);
        }

        return tag;
    }

    private bool ResolveSettled(AmqpDeliveryOptions? options)
    {
        bool? requested = options?.Settled;

        // The link's sender settlement mode decides unless it is mixed (AMQP 1.0 Part 2, section 2.8.2).
        switch (SenderSettleMode)
        {
            case AmqpSenderSettleMode.Settled when requested is false:
            case AmqpSenderSettleMode.Unsettled when requested is true:
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliverySettleModeConflict, SenderSettleMode),
                    nameof(options));
            case AmqpSenderSettleMode.Settled:
                return true;
            case AmqpSenderSettleMode.Unsettled:
                return false;
            default:
                return requested ?? false;
        }
    }

    private bool TryAutoCompleteDrainLocked()
    {
        // A drain completes on its own once no send waits to use the credit.
        if (!_drain || !_autoCompleteDrain || _pendingSends > 0 || _linkCredit == 0 || State != AmqpLinkState.Attached)
        {
            return false;
        }

        CompleteDrainLocked();
        return true;
    }

    private void CompleteDrainLocked()
    {
        // Using up the credit advances the delivery-count as if that many deliveries were sent, and the flow
        // tells the receiver so (AMQP 1.0 Part 2, section 2.6.7).
        _deliveryCount = unchecked(_deliveryCount + _linkCredit);
        _linkCredit = 0;
        SessionEndpoint.WriteFlowLocked(this);
    }

    private Task? GetTransferWaitLocked(bool started)
    {
        // Credit is taken by the first frame of a delivery; every frame takes a place in the peer's window.
        if (!started && _linkCredit == 0)
        {
            return _credit.WaitAsync();
        }

        AmqpSessionEndpoint session = SessionEndpoint;

        return session.HasOutgoingWindowLocked ? session.Writer.WaitForCapacity() : session.WaitForWindowLocked();
    }

    private async ValueTask<AmqpOutgoingDelivery?> TransferAsync(
        ReadOnlyMemory<byte> payload,
        AmqpDeliveryOptions? options,
        bool settled,
        bool abort,
        CancellationToken cancellationToken)
    {
        AmqpOutgoingDelivery? delivery = null;
        int offset = 0;
        bool started = false;

        while (true)
        {
            Task? wait;

            lock (Sync)
            {
                if (State != AmqpLinkState.Attached)
                {
                    StopTrackingUnfinishedLocked(delivery);
                    throw GetDetachedExceptionLocked();
                }

                wait = GetTransferWaitLocked(started);

                if (wait is null)
                {
                    AmqpSessionEndpoint session = SessionEndpoint;
                    bool complete;

                    if (started)
                    {
                        complete = WriteTransferFrameLocked(payload.Span, ref offset);
                    }
                    else
                    {
                        complete = StartDeliveryLocked(payload.Span, options, settled, abort, ref offset, out delivery);
                        started = true;
                    }

                    while (!complete && session.HasOutgoingWindowLocked && session.Writer.HasCapacity)
                    {
                        complete = WriteTransferFrameLocked(payload.Span, ref offset);
                    }

                    session.Writer.Schedule();

                    if (complete)
                    {
                        return delivery;
                    }

                    continue;
                }
            }

            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (started && cancellationToken.IsCancellationRequested)
            {
                // Part of the delivery is on the wire: abort it, so the receiver discards what it has
                // (AMQP 1.0 Part 2, section 2.6.14).
                await AbortAsync(delivery).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Writes the first frame of a delivery and commits the delivery: it takes a delivery-id and a unit of credit.
    /// </summary>
    /// <param name="payload">The delivery's payload.</param>
    /// <param name="options">The delivery's options.</param>
    /// <param name="settled">Whether the delivery is sent settled.</param>
    /// <param name="abort">Whether the frame aborts a delivery carried over from an earlier attachment, and is its only one.</param>
    /// <param name="offset">The offset of the payload the next frame starts at.</param>
    /// <param name="delivery">The delivery, when it waits for an outcome.</param>
    /// <returns><see langword="true"/> when the frame carried the whole payload.</returns>
    /// <exception cref="ArgumentException">Thrown, before anything is committed, when the delivery state leaves no room for the payload in a frame, or a send ahead of this one resumed the same delivery.</exception>
    private bool StartDeliveryLocked(
        ReadOnlySpan<byte> payload,
        AmqpDeliveryOptions? options,
        bool settled,
        bool abort,
        ref int offset,
        out AmqpOutgoingDelivery? delivery)
    {
        ThrowIfUnsettledRuleBrokenLocked(options, abort);

        AmqpSessionEndpoint session = SessionEndpoint;
        AmqpDeliveryTag tag = options?.DeliveryTag ?? NextGeneratedTagLocked();
        AmqpTransferPerformative transfer = _transfer;
        bool complete;

        transfer.Handle = Handle;
        transfer.DeliveryId = session.NextDeliveryIdLocked;
        transfer.DeliveryTag = tag;
        transfer.MessageFormat = options?.MessageFormat ?? 0;
        transfer.Settled = settled;
        transfer.ReceiverSettleMode = null;
        transfer.State = options?.State;
        transfer.Resume = options?.Resume ?? false;
        transfer.Aborted = abort;
        transfer.Batchable = false;

        try
        {
            complete = WriteTransferFrameLocked(payload, ref offset);
        }
        finally
        {
            // Later frames of the delivery carry only the handle and the more flag, and a transactional state:
            // every frame of a delivery names its transaction, an aborting one included (AMQP 1.0 Part 4,
            // section 4.4.1).
            transfer.DeliveryId = null;
            transfer.DeliveryTag = null;
            transfer.MessageFormat = null;
            transfer.Settled = null;
            transfer.State = transfer.State as AmqpTransactionalState;
            transfer.Resume = false;
            transfer.Aborted = false;
        }

        uint deliveryId = session.AllocateDeliveryIdLocked();

        if (options?.DeliveryTag is null)
        {
            _nextTag++;
        }
        else if (options.Resume)
        {
            // A delivery is resumed once per attachment: from here on it is a delivery of this one.
            TakeResumableLocked(tag);
        }

        _linkCredit--;
        _deliveryCount = unchecked(_deliveryCount + 1);

        // An aborted delivery is settled implicitly (AMQP 1.0 Part 2, section 2.6.14).
        if (settled || abort)
        {
            delivery = null;
        }
        else
        {
            delivery = new AmqpOutgoingDelivery(this, deliveryId, tag);
            session.AddOutgoingLocked(delivery);
        }

        return complete;
    }

    /// <summary>
    /// Writes one <c>transfer</c> frame of the delivery in progress, with as much of the payload as fits.
    /// </summary>
    /// <returns><see langword="true"/> when the frame was the delivery's last.</returns>
    private bool WriteTransferFrameLocked(ReadOnlySpan<byte> payload, ref int offset)
    {
        AmqpSessionEndpoint session = SessionEndpoint;
        AmqpBufferWriter buffer = session.Writer.Buffer;
        int maxFrameSize = (int)Math.Min(session.MaxFrameSize, int.MaxValue);
        int remaining = payload.Length - offset;
        int start = AmqpFrame.Begin(buffer, AmqpFrame.AmqpType, session.Channel);

        _transfer.More = true;

        try
        {
            _transfer.Encode(buffer);
        }
        catch (Exception)
        {
            // Justified catch-all: a delivery state the encoder refuses must not leave a partial frame
            // behind to corrupt the frames after it. Later encodings of the same state cannot fail.
            buffer.Truncate(start);
            throw;
        }

        int room = maxFrameSize - (buffer.Length - start);

        if (remaining > room)
        {
            if (room <= 0)
            {
                buffer.Truncate(start);

                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryStateTooLarge, maxFrameSize),
                    "options");
            }

            buffer.Write(payload.Slice(offset, room));
            offset += room;
            AmqpFrame.End(buffer, start);
            session.OnTransferWrittenLocked();
            return false;
        }

        // The rest fits: encode the performative again as the delivery's last frame, which is never larger.
        buffer.Truncate(start);
        start = AmqpFrame.Begin(buffer, AmqpFrame.AmqpType, session.Channel);
        _transfer.More = false;
        _transfer.Encode(buffer);
        buffer.Write(payload.Slice(offset));
        offset = payload.Length;
        AmqpFrame.End(buffer, start);
        session.OnTransferWrittenLocked();
        return true;
    }

    private async ValueTask AbortAsync(AmqpOutgoingDelivery? delivery)
    {
        while (true)
        {
            Task wait;

            lock (Sync)
            {
                AmqpSessionEndpoint session = SessionEndpoint;

                // A link that stopped took the delivery with it.
                if (State != AmqpLinkState.Attached)
                {
                    StopTrackingUnfinishedLocked(delivery);
                    return;
                }

                if (session.HasOutgoingWindowLocked)
                {
                    WriteAbortLocked();

                    // An aborted delivery is settled implicitly (AMQP 1.0 Part 2, section 2.7.5).
                    if (delivery is not null)
                    {
                        session.RemoveOutgoingLocked(delivery);
                    }

                    session.Writer.Schedule();
                    return;
                }

                wait = session.WaitForWindowLocked();
            }

            await wait.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops tracking a delivery whose frames were not all sent when the link stopped. It cannot complete on this
    /// attachment, so its send fails at once even when the link's deliveries stay live: resuming it on a later
    /// attachment is the application's (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    private void StopTrackingUnfinishedLocked(AmqpOutgoingDelivery? delivery)
    {
        if (delivery is not null)
        {
            SessionEndpoint.RemoveOutgoingLocked(delivery);
        }
    }

    private void WriteAbortLocked()
    {
        AmqpSessionEndpoint session = SessionEndpoint;
        AmqpBufferWriter buffer = session.Writer.Buffer;
        int start = AmqpFrame.Begin(buffer, AmqpFrame.AmqpType, session.Channel);

        _transfer.More = false;
        _transfer.Aborted = true;
        _transfer.Encode(buffer);
        _transfer.Aborted = false;
        AmqpFrame.End(buffer, start);
        session.OnTransferWrittenLocked();
    }
}
