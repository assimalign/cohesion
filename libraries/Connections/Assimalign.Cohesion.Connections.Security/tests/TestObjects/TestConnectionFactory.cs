using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// A <see cref="ConnectionFactory"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionFactory : ConnectionFactory
{
    private readonly Queue<Connection> _pending = new();

    public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

    public void Enqueue(Connection connection) => _pending.Enqueue(connection);

    public override ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());
}
