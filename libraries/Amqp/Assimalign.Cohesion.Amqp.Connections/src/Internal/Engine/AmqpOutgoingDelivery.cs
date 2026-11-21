using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// A delivery a sender sent unsettled, tracked by its session until the receiver's outcome settles it.
/// </summary>
[DebuggerDisplay("AmqpOutgoingDelivery {DeliveryId} ({DeliveryTag})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpOutgoingDelivery
{
    private readonly AmqpSenderEndpoint _sender;
    private readonly uint _deliveryId;
    private readonly AmqpDeliveryTag _deliveryTag;
    private readonly TaskCompletionSource<AmqpDeliveryState?> _completion;

    public AmqpOutgoingDelivery(AmqpSenderEndpoint sender, uint deliveryId, AmqpDeliveryTag deliveryTag)
    {
        _sender = sender;
        _deliveryId = deliveryId;
        _deliveryTag = deliveryTag;
        _completion = new TaskCompletionSource<AmqpDeliveryState?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Gets the sender the delivery was sent on.
    /// </summary>
    public AmqpSenderEndpoint Sender => _sender;

    /// <summary>
    /// Gets the delivery-id the session numbered the delivery with.
    /// </summary>
    public uint DeliveryId => _deliveryId;

    /// <summary>
    /// Gets the delivery tag.
    /// </summary>
    public AmqpDeliveryTag DeliveryTag => _deliveryTag;

    /// <summary>
    /// Gets or sets the last delivery state the receiver reported, under the connection's lock.
    /// </summary>
    public AmqpDeliveryState? RemoteState { get; set; }

    /// <summary>
    /// Gets a task that completes with the receiver's outcome once the delivery settles.
    /// </summary>
    public Task<AmqpDeliveryState?> Completion => _completion.Task;

    /// <summary>
    /// Completes the delivery with the state it settled with.
    /// </summary>
    public void Complete(AmqpDeliveryState? state)
    {
        _completion.TrySetResult(state);
    }

    /// <summary>
    /// Fails the delivery, because its link stopped before it settled.
    /// </summary>
    public void Fail(Exception exception)
    {
        if (_completion.TrySetException(exception))
        {
            // Mark the failure observed: a caller that stopped waiting for the outcome never reads it.
            _ = _completion.Task.Exception;
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpOutgoingDelivery _delivery;

        public DebugView(AmqpOutgoingDelivery delivery)
        {
            _delivery = delivery;
        }

        public uint DeliveryId => _delivery.DeliveryId;
        public AmqpDeliveryTag DeliveryTag => _delivery.DeliveryTag;
        public AmqpDeliveryState? RemoteState => _delivery.RemoteState;
        public bool IsSettled => _delivery.Completion.IsCompleted;
    }
}
