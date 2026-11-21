using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using NetLite = Amqp;
using NetLiteFraming = Amqp.Framing;
using NetLiteListener = Amqp.Listener;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An AMQPNetLite message processor: the node a Cohesion sender attaches to on an <see cref="AmqpNetLiteHost"/>.
/// It keeps every message it receives, as the peer decoded it, and settles each one with the state
/// <see cref="Settle"/> chooses (accepted when it chooses none).
/// </summary>
internal sealed class TestMessageProcessor : NetLiteListener.IMessageProcessor
{
    private readonly Channel<NetLite.Message> _messages = Channel.CreateUnbounded<NetLite.Message>();

    /// <summary>
    /// Gets the credit the peer grants each link attached to this node.
    /// </summary>
    public int Credit { get; init; } = 100;

    /// <summary>
    /// Gets the state each message is settled with: <see langword="null"/> accepts it, a
    /// <see cref="NetLiteFraming.Rejected"/> rejects it with its error, and any other outcome is sent as it is.
    /// </summary>
    public Func<NetLite.Message, NetLiteFraming.DeliveryState?>? Settle { get; init; }

    /// <summary>
    /// Waits for the next message the node received.
    /// </summary>
    public ValueTask<NetLite.Message> ReceiveAsync(CancellationToken cancellationToken)
    {
        return _messages.Reader.ReadAsync(cancellationToken);
    }

    public void Process(NetLiteListener.MessageContext messageContext)
    {
        NetLite.Message message = messageContext.Message;
        NetLiteFraming.DeliveryState? state = Settle?.Invoke(message);

        _messages.Writer.TryWrite(message);

        switch (state)
        {
            case null:
                messageContext.Complete();
                break;
            case NetLiteFraming.Rejected rejected:
                messageContext.Complete(rejected.Error);
                break;
            default:
                messageContext.Link.DisposeMessage(message, state, true);
                break;
        }
    }
}
