using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using NetLite = Amqp;
using NetLiteFraming = Amqp.Framing;
using NetLiteListener = Amqp.Listener;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An AMQPNetLite message source: the node a Cohesion receiver attaches to on an <see cref="AmqpNetLiteHost"/>.
/// It sends the messages the test enqueues, in order, and keeps the state each one was settled with.
/// </summary>
/// <remarks>
/// With nothing queued, the source waits briefly and then reports that it has no message. AMQPNetLite's source
/// endpoint asks again while the link has credit, and completes a drain the receiver asked for only on such an
/// answer, so a source that waited for a message forever would never let a drain finish.
/// </remarks>
internal sealed class TestMessageSource : NetLiteListener.IMessageSource
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Channel<NetLite.Message> _pending = Channel.CreateUnbounded<NetLite.Message>();
    private readonly Channel<NetLiteFraming.DeliveryState?> _dispositions = Channel.CreateUnbounded<NetLiteFraming.DeliveryState?>();

    /// <summary>
    /// Queues a message for the next attached link that has credit.
    /// </summary>
    public void Enqueue(NetLite.Message message)
    {
        _pending.Writer.TryWrite(message);
    }

    /// <summary>
    /// Waits for the state the receiver settled the next message with.
    /// </summary>
    public ValueTask<NetLiteFraming.DeliveryState?> ReceiveDispositionAsync(CancellationToken cancellationToken)
    {
        return _dispositions.Reader.ReadAsync(cancellationToken);
    }

    public async Task<NetLiteListener.ReceiveContext?> GetMessageAsync(NetLiteListener.ListenerLink link)
    {
        if (!_pending.Reader.TryRead(out NetLite.Message? message))
        {
            using CancellationTokenSource poll = new(_pollInterval);

            try
            {
                await _pending.Reader.WaitToReadAsync(poll.Token);
            }
            catch (OperationCanceledException)
            {
                // Nothing arrived: report no message, which lets a draining link complete its drain.
            }

            if (!_pending.Reader.TryRead(out message))
            {
                return null;
            }
        }

        return new NetLiteListener.ReceiveContext(link, message);
    }

    public void DisposeMessage(NetLiteListener.ReceiveContext receiveContext, NetLiteListener.DispositionContext dispositionContext)
    {
        _dispositions.Writer.TryWrite(dispositionContext.DeliveryState);
        dispositionContext.Complete();
    }
}
