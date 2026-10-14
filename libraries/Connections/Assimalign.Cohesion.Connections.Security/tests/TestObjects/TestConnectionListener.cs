using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// A <see cref="DuplexConnectionListener"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionListener : DuplexConnectionListener
{
    private readonly Queue<IDuplexConnection> _pending = new();

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 22000);

    public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

    public void Enqueue(IDuplexConnection connection) => _pending.Enqueue(connection);

    protected override ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
