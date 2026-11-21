using System.Threading.Tasks;

using NetLiteFraming = Amqp.Framing;
using NetLiteHandler = Amqp.Handler;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An AMQPNetLite protocol handler that keeps the <c>open</c> and the <c>close</c> an AMQPNetLite connection
/// received, exactly as the peer decoded them.
/// </summary>
/// <remarks>
/// The handler is how a test sees what Cohesion answered. AMQPNetLite's own close reporting cannot show it: a
/// connection's <c>Error</c> keeps the error the peer closed with when the answering <c>close</c> carries none, so
/// <c>CloseAsync(timeout, error)</c> faults with the error it sent either way.
/// </remarks>
internal sealed class AmqpNetLiteConnectionEvents : NetLiteHandler.IHandler
{
    private readonly TaskCompletionSource<NetLiteFraming.Open> _remoteOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NetLiteFraming.Close> _remoteClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the <c>open</c> the connection received.
    /// </summary>
    public Task<NetLiteFraming.Open> RemoteOpen => _remoteOpen.Task;

    /// <summary>
    /// Gets the <c>close</c> the connection received.
    /// </summary>
    public Task<NetLiteFraming.Close> RemoteClose => _remoteClose.Task;

    public bool CanHandle(NetLiteHandler.EventId id)
    {
        // Every other event changes what the peer does once a handler claims it (socket and TLS setup among them).
        return id is NetLiteHandler.EventId.ConnectionRemoteOpen or NetLiteHandler.EventId.ConnectionRemoteClose;
    }

    public void Handle(NetLiteHandler.Event protocolEvent)
    {
        switch (protocolEvent.Id)
        {
            case NetLiteHandler.EventId.ConnectionRemoteOpen:
                _remoteOpen.TrySetResult((NetLiteFraming.Open)protocolEvent.Context);
                break;
            case NetLiteHandler.EventId.ConnectionRemoteClose:
                _remoteClose.TrySetResult((NetLiteFraming.Close)protocolEvent.Context);
                break;
        }
    }
}
