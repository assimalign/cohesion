using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// A delivery a receiver reassembled from its <c>transfer</c> frames, and the application's means of
/// settling it (AMQP 1.0 Part 2, section 2.6.12).
/// </summary>
/// <remarks>
/// The receiver builds the delivery under the connection's lock and hands it to the application once its
/// last frame arrived; from then on its payload never changes. An unsettled delivery is tracked by the
/// session until it settles.
/// </remarks>
[DebuggerDisplay("AmqpDelivery {DeliveryTag} (Settled = {IsSettled})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpIncomingDelivery : IAmqpDelivery
{
    private readonly AmqpReceiverEndpoint _receiver;
    private readonly uint _deliveryId;
    private readonly AmqpDeliveryTag _deliveryTag;
    private readonly uint _messageFormat;
    private readonly bool _resumed;
    private readonly bool _settlesFirst;
    private AmqpBufferWriter? _builder;
    private ReadOnlyMemory<byte> _payload;
    private long _length;
    private bool _settled;
    private bool _settling;
    private AmqpDeliveryState? _remoteState;
    private TaskCompletionSource? _settlement;
    private AmqpException? _failure;

    public AmqpIncomingDelivery(
        AmqpReceiverEndpoint receiver,
        uint deliveryId,
        AmqpDeliveryTag deliveryTag,
        uint messageFormat,
        bool settled,
        AmqpDeliveryState? remoteState,
        bool resumed,
        bool settlesFirst)
    {
        _receiver = receiver;
        _deliveryId = deliveryId;
        _deliveryTag = deliveryTag;
        _messageFormat = messageFormat;
        _settled = settled;
        _remoteState = remoteState;
        _resumed = resumed;
        _settlesFirst = settlesFirst;
    }

    public IAmqpReceiver Receiver => _receiver;

    /// <summary>
    /// Gets the receiving endpoint the delivery arrived on.
    /// </summary>
    internal AmqpReceiverEndpoint ReceiverEndpoint => _receiver;

    public AmqpDeliveryTag DeliveryTag => _deliveryTag;

    public uint MessageFormat => _messageFormat;

    public ReadOnlyMemory<byte> Payload => _payload;

    public bool IsSettled => Volatile.Read(ref _settled);

    public AmqpDeliveryState? RemoteState => Volatile.Read(ref _remoteState);

    public bool IsResumed => _resumed;

    /// <summary>
    /// Gets the delivery-id the peer's session numbered the delivery with.
    /// </summary>
    internal uint DeliveryId => _deliveryId;

    /// <summary>
    /// Gets a value indicating whether the receiver settles the delivery first: the link's receiver
    /// settlement mode is first, or the delivery's transfer asked for it.
    /// </summary>
    internal bool SettlesFirst => _settlesFirst;

    /// <summary>
    /// Gets a value indicating whether the application asked to settle the delivery.
    /// </summary>
    internal bool IsSettling => _settling;

    /// <summary>
    /// Gets the reason the delivery can no longer be settled, once its link stopped.
    /// </summary>
    internal AmqpException? Failure => _failure;

    public AmqpMessage GetMessage()
    {
        return AmqpMessageEncoding.Decode(_payload);
    }

    public ValueTask AcceptAsync(CancellationToken cancellationToken = default)
    {
        return _receiver.SettleAsync(this, AmqpAccepted.Instance, cancellationToken);
    }

    public ValueTask RejectAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        return _receiver.SettleAsync(this, new AmqpRejected(error), cancellationToken);
    }

    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        return _receiver.SettleAsync(this, AmqpReleased.Instance, cancellationToken);
    }

    public ValueTask ModifyAsync(
        bool deliveryFailed,
        bool undeliverableHere = false,
        IReadOnlyDictionary<AmqpSymbol, object?>? messageAnnotations = null,
        CancellationToken cancellationToken = default)
    {
        return _receiver.SettleAsync(this, new AmqpModified(deliveryFailed, undeliverableHere, messageAnnotations), cancellationToken);
    }

    public ValueTask SettleAsync(AmqpDeliveryState? state, CancellationToken cancellationToken = default)
    {
        return _receiver.SettleAsync(this, state, cancellationToken);
    }

    /// <summary>
    /// Appends the payload of a <c>transfer</c> frame.
    /// </summary>
    /// <returns><see langword="false"/> when the delivery grew past the link's maximum message size.</returns>
    internal bool AppendLocked(ReadOnlySpan<byte> payload, bool more, ulong? maxMessageSize)
    {
        _length += payload.Length;

        if (maxMessageSize is ulong max && (ulong)_length > max)
        {
            DiscardLocked();
            return false;
        }

        if (_builder is null && !more && _length == payload.Length)
        {
            // A delivery in one frame is copied once, straight into its payload.
            _payload = payload.ToArray();
            return true;
        }

        _builder ??= new AmqpBufferWriter();
        _builder.Write(payload);
        return true;
    }

    /// <summary>
    /// Returns whether a state the sender sends after the delivery's first frame keeps the delivery in the
    /// transaction that frame named, or out of any: every frame of a delivery belongs to the same transaction
    /// (AMQP 1.0 Part 4, section 4.4.1), and a later state may not move it.
    /// </summary>
    internal bool KeepsTransaction(AmqpDeliveryState state)
    {
        return RemoteState is AmqpTransactionalState current
            ? state is AmqpTransactionalState next && next.TransactionId.Span.SequenceEqual(current.TransactionId.Span)
            : state is not AmqpTransactionalState;
    }

    /// <summary>
    /// Applies a later <c>transfer</c> frame's settled flag and state.
    /// </summary>
    internal void OnContinuationLocked(bool settled, AmqpDeliveryState? state)
    {
        if (state is not null)
        {
            Volatile.Write(ref _remoteState, state);
        }

        if (settled)
        {
            Volatile.Write(ref _settled, true);
        }
    }

    /// <summary>
    /// Fixes the payload once the delivery's last frame arrived.
    /// </summary>
    internal void CompleteLocked()
    {
        if (_builder is not null)
        {
            _payload = _builder.ToArray();
            _builder.Dispose();
            _builder = null;
        }
    }

    /// <summary>
    /// Releases what an aborted or oversized delivery buffered.
    /// </summary>
    internal void DiscardLocked()
    {
        _builder?.Dispose();
        _builder = null;
    }

    /// <summary>
    /// Records a settlement the receiver sent settled.
    /// </summary>
    internal void SettleLocked()
    {
        _settling = true;
        Volatile.Write(ref _settled, true);
    }

    /// <summary>
    /// Records an outcome the receiver sent unsettled and returns a task that completes when the sender settles.
    /// </summary>
    internal Task BeginSettleLocked()
    {
        _settling = true;
        _settlement ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _settlement.Task;
    }

    /// <summary>
    /// Applies a <c>disposition</c> the sender sent for the delivery.
    /// </summary>
    internal void OnRemoteDispositionLocked(bool settled, AmqpDeliveryState? state)
    {
        if (state is not null)
        {
            Volatile.Write(ref _remoteState, state);
        }

        if (settled)
        {
            Volatile.Write(ref _settled, true);
            _settlement?.TrySetResult();
        }
    }

    /// <summary>
    /// Fails the delivery's settlement, because its link stopped first.
    /// </summary>
    internal void FailLocked(AmqpException exception)
    {
        _failure = exception;

        if (_settlement is not null && _settlement.TrySetException(exception))
        {
            _ = _settlement.Task.Exception;
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpIncomingDelivery _delivery;

        public DebugView(AmqpIncomingDelivery delivery)
        {
            _delivery = delivery;
        }

        public IAmqpReceiver Receiver => _delivery.Receiver;
        public AmqpDeliveryTag DeliveryTag => _delivery.DeliveryTag;
        public uint MessageFormat => _delivery.MessageFormat;
        public int PayloadLength => _delivery.Payload.Length;
        public bool IsSettled => _delivery.IsSettled;
        public AmqpDeliveryState? RemoteState => _delivery.RemoteState;
        public bool IsResumed => _delivery.IsResumed;
    }
}
