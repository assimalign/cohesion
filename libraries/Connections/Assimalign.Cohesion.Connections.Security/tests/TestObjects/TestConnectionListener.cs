using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// A <see cref="ConnectionListener"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionListener : ConnectionListener
{
    private readonly Queue<Connection> _pending = new();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 22000);

    public override ConnectionCapabilities Capabilities => TestPipeConnection.DefaultCapabilities;

    public void Enqueue(Connection connection) => _pending.Enqueue(connection);

    public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
