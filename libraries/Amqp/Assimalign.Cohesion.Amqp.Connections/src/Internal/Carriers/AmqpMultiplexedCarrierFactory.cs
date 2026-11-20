using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Adapts a multiplexed carrier factory to the duplex factory shape the Connections layered factory
/// consumes, so both carrier kinds share one establishment path. Each connected multiplexed connection
/// becomes an <see cref="AmqpMultiplexedCarrier"/> whose AMQP stream the establishment step opens.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("AmqpMultiplexedCarrierFactory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpMultiplexedCarrierFactory : DuplexConnectionFactory
{
    private readonly IMultiplexedConnectionFactory _inner;

    public AmqpMultiplexedCarrierFactory(IMultiplexedConnectionFactory inner)
    {
        _inner = inner;
    }

    public override ConnectionCapabilities Capabilities => _inner.Capabilities with { IsMultiplexed = false };

    protected override async ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        IMultiplexedConnection connection = await _inner.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        return new AmqpMultiplexedCarrier(connection, opensStream: true);
    }

    private sealed class DebugView
    {
        private readonly AmqpMultiplexedCarrierFactory _target;

        public DebugView(AmqpMultiplexedCarrierFactory value)
        {
            _target = value;
        }

        public ConnectionCapabilities Capabilities => _target.Capabilities;
    }
}
