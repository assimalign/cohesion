using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A <see cref="ConnectionListener"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionListener : ConnectionListener
{
    private readonly Queue<Connection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionListener(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;
    }

    public bool IsDisposed { get; private set; }

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 15000);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(Connection connection) => _pending.Enqueue(connection);

    public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pending.Dequeue());

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
