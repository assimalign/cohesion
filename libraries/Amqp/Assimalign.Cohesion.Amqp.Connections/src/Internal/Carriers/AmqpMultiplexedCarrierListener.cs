using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Adapts a multiplexed carrier listener to the duplex listener shape the Connections layered listener
/// consumes, so both carrier kinds share one establishment pump. Each accepted multiplexed connection
/// becomes an <see cref="AmqpMultiplexedCarrier"/> whose AMQP stream the establishment step accepts
/// from the peer.
/// </summary>
/// <remarks>
/// The adapter does no per-connection work of its own: waiting for the peer's stream is part of
/// establishment, so it runs under the handshake timeout on the pump's task, never here.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpMultiplexedCarrierListener {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpMultiplexedCarrierListener : DuplexConnectionListener
{
    private readonly IMultiplexedConnectionListener _inner;

    public AmqpMultiplexedCarrierListener(IMultiplexedConnectionListener inner)
    {
        _inner = inner;
    }

    // The adapter represents the same bound endpoint, so it reports the inner listener's id.
    public override ListenerId Id => _inner.Id;

    public override EndPoint EndPoint => _inner.EndPoint;

    public override ConnectionCapabilities Capabilities => _inner.Capabilities with { IsMultiplexed = false };

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        return _inner.BindAsync(cancellationToken);
    }

    protected override async ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
    {
        IMultiplexedConnection connection = await _inner.AcceptAsync(cancellationToken).ConfigureAwait(false);

        return new AmqpMultiplexedCarrier(connection, opensStream: false);
    }

    public override ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    private sealed class DebugView
    {
        private readonly AmqpMultiplexedCarrierListener _target;

        public DebugView(AmqpMultiplexedCarrierListener value)
        {
            _target = value;
        }

        public ListenerId Id => _target.Id;
        public EndPoint EndPoint => _target.EndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public global::System.Collections.Generic.IReadOnlyCollection<IDuplexConnection> Connections => _target.Connections;
    }
}
