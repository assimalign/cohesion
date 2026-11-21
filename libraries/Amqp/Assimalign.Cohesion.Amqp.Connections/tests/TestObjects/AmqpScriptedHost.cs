using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An AMQP connection a listener accepted from a scripted peer, which a test drives frame by frame.
/// </summary>
/// <remarks>
/// Disposal hangs up the peer first, so closing the connection does not wait for a <c>close</c> the
/// scripted peer would never send.
/// </remarks>
internal sealed class AmqpScriptedHost : IAsyncDisposable
{
    private AmqpScriptedHost(AmqpTestPeer peer, AmqpConnectionListener listener, IAmqpConnection connection, AmqpOpenPerformative engineOpen)
    {
        Peer = peer;
        Listener = listener;
        Connection = connection;
        EngineOpen = engineOpen;
    }

    public AmqpTestPeer Peer { get; }

    public AmqpConnectionListener Listener { get; }

    /// <summary>
    /// Gets the connection the listener accepted from the scripted peer.
    /// </summary>
    public IAmqpConnection Connection { get; }

    /// <summary>
    /// Gets the <c>open</c> the engine sent the scripted peer.
    /// </summary>
    public AmqpOpenPerformative EngineOpen { get; }

    /// <summary>
    /// Opens a connection from a scripted peer to a new listener.
    /// </summary>
    public static async Task<AmqpScriptedHost> AcceptAsync(
        AmqpConnectionListenerOptions? options = null,
        AmqpOpenPerformative? peerOpen = null,
        CancellationToken cancellationToken = default)
    {
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, cancellationToken);

        try
        {
            AmqpOpenPerformative engineOpen = await peer.OpenAsClientAsync(peerOpen, cancellationToken);
            IAmqpConnection connection = await accept;

            return new AmqpScriptedHost(peer, listener, connection, engineOpen);
        }
        catch
        {
            peer.Carrier.CompletePeerOutput();
            await listener.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Peer.Carrier.CompletePeerOutput();
        await Connection.DisposeAsync();
        await Listener.DisposeAsync();
    }
}
