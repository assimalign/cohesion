using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A <see cref="DuplexConnectionListener"/> double that yields pre-queued connections.
/// </summary>
internal sealed class TestConnectionListener : DuplexConnectionListener
{
    private readonly Queue<DuplexConnection> _pending = new();
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionListener(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;
    }

    public bool IsDisposed { get; private set; }

    public int BindCount { get; private set; }

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 15000);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(DuplexConnection connection) => _pending.Enqueue(connection);

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindCount++;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IDuplexConnection>(_pending.Dequeue());

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
