using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Establishes the AMQP connections a listener accepts: on a multiplexed carrier it accepts the AMQP
/// stream, then it runs the listening side of the handshake, all within the handshake timeout.
/// </summary>
/// <remarks>
/// <see cref="AmqpConnectionListener"/> composes it onto its carrier listener with
/// <c>listener.Use(layer, maxConcurrentUpgrades)</c>: each establishment runs on its own task, a bounded
/// number at a time, and one that fails or times out ends only its carrier and is reported through the
/// <c>Assimalign.Cohesion.Connections</c> event source. The carrier stays the caller's on failure, as
/// <see cref="IConnectionLayer"/> requires; on success the AMQP connection owns it.
/// </remarks>
[DebuggerDisplay("AmqpConnectionLayer")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpConnectionLayer : IConnectionLayer
{
    private readonly AmqpConnectionSettings _settings;

    public AmqpConnectionLayer(AmqpConnectionSettings settings)
    {
        _settings = settings;
    }

    // An AMQP connection runs over its carrier's byte stream: establishing it changes no capability.
    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities)
    {
        return capabilities;
    }

    public async ValueTask<IDuplexConnection> UpgradeAsync(IDuplexConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await AmqpHandshake.EstablishAsync(connection, _settings, connecting: false, hostName: null, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        public DebugView(AmqpConnectionLayer layer)
        {
        }
    }
}
