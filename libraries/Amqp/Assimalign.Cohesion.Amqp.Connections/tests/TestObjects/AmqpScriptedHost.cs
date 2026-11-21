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

    /// <summary>
    /// Opens two connections to one new listener, each from its own scripted peer. Both peers send the same
    /// container identifier, so a link is the same link whichever connection attaches it. Disposing either host
    /// disposes the shared listener.
    /// </summary>
    public static async Task<(AmqpScriptedHost First, AmqpScriptedHost Second)> AcceptPairAsync(
        AmqpConnectionListenerOptions? options,
        CancellationToken cancellationToken)
    {
        TestConnection firstCarrier = new();
        TestConnection secondCarrier = new();
        TestConnectionListener carrierListener = new();

        carrierListener.Enqueue(firstCarrier);
        carrierListener.Enqueue(secondCarrier);

        AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener, options);
        AmqpTestPeer firstPeer = new(firstCarrier);
        AmqpTestPeer secondPeer = new(secondCarrier);
        IAmqpConnection? first = null;

        try
        {
            // Only the peer that opened has written anything, so each accept returns that peer's connection.
            Task<IAmqpConnection> acceptFirst = listener.AcceptAsync(cancellationToken).AsTask();
            AmqpOpenPerformative firstOpen = await firstPeer.OpenAsClientAsync(null, cancellationToken);
            first = await acceptFirst;

            Task<IAmqpConnection> acceptSecond = listener.AcceptAsync(cancellationToken).AsTask();
            AmqpOpenPerformative secondOpen = await secondPeer.OpenAsClientAsync(null, cancellationToken);
            IAmqpConnection second = await acceptSecond;

            return (new AmqpScriptedHost(firstPeer, listener, first, firstOpen), new AmqpScriptedHost(secondPeer, listener, second, secondOpen));
        }
        catch
        {
            firstCarrier.CompletePeerOutput();
            secondCarrier.CompletePeerOutput();

            if (first is not null)
            {
                await first.DisposeAsync();
            }

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
