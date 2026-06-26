using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="MultiplexedConnectionListener"/> double that yields pre-queued multiplexed
/// carrier connections and records when it is disposed.
/// </summary>
internal sealed class TestMultiplexedConnectionListener : MultiplexedConnectionListener
{
    private readonly Queue<MultiplexedConnection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestMultiplexedConnectionListener(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestMultiplexedConnection.DefaultCapabilities;
    }

    public bool IsDisposed { get; private set; }

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5671);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(MultiplexedConnection connection) => _pending.Enqueue(connection);

    public override ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
