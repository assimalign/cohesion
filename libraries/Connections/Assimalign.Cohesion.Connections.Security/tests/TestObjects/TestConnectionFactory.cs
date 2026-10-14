using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// A <see cref="DuplexConnectionFactory"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionFactory : DuplexConnectionFactory
{
    private readonly Queue<IDuplexConnection> _pending = new();

    public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

    public void Enqueue(IDuplexConnection connection) => _pending.Enqueue(connection);

    protected override ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());
}
