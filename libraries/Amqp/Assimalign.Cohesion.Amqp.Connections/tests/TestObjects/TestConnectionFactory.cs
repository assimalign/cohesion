using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="DuplexConnectionFactory"/> double that yields pre-queued carrier connections and
/// records the endpoint each connect call was made against.
/// </summary>
internal sealed class TestConnectionFactory : DuplexConnectionFactory
{
    private readonly Queue<IDuplexConnection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionFactory(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;
    }

    public EndPoint? LastEndPoint { get; private set; }

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(IDuplexConnection connection) => _pending.Enqueue(connection);

    protected override ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        LastEndPoint = endPoint;

        return ValueTask.FromResult(_pending.Dequeue());
    }
}
