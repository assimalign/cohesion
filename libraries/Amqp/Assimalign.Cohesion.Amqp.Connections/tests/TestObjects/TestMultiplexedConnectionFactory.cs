using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="MultiplexedConnectionFactory"/> double that yields pre-queued multiplexed
/// carrier connections and records the endpoint each connect call was made against.
/// </summary>
internal sealed class TestMultiplexedConnectionFactory : MultiplexedConnectionFactory
{
    private readonly Queue<IMultiplexedConnection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestMultiplexedConnectionFactory(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestMultiplexedConnection.DefaultCapabilities;
    }

    public EndPoint? LastEndPoint { get; private set; }

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(IMultiplexedConnection connection) => _pending.Enqueue(connection);

    protected override ValueTask<IMultiplexedConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        LastEndPoint = endPoint;

        return ValueTask.FromResult(_pending.Dequeue());
    }
}
