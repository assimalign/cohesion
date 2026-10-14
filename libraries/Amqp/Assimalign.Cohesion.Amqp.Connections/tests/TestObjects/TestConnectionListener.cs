using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="DuplexConnectionListener"/> double that yields pre-queued carrier connections and
/// records when it is disposed.
/// </summary>
internal sealed class TestConnectionListener : DuplexConnectionListener
{
    private readonly Queue<IDuplexConnection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionListener(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;
    }

    public bool IsDisposed { get; private set; }

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5672);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(IDuplexConnection connection) => _pending.Enqueue(connection);

    protected override ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
