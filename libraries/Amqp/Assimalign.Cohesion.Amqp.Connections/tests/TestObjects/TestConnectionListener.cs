using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="DuplexConnectionListener"/> double that hands out the carrier connections a test queues.
/// Like a real listener, an accept waits while nothing is queued, until a connection is queued, the accept
/// is canceled, or the listener is disposed. It records binds and disposal.
/// </summary>
internal sealed class TestConnectionListener : DuplexConnectionListener
{
    private readonly Channel<IDuplexConnection> _pending = Channel.CreateUnbounded<IDuplexConnection>();
    private readonly ConnectionCapabilities _capabilities;
    private int _bindCount;
    private volatile bool _isDisposed;

    public TestConnectionListener(ConnectionCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;
    }

    public int BindCount => Volatile.Read(ref _bindCount);

    public bool IsDisposed => _isDisposed;

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5672);

    public override ConnectionCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Queues a carrier connection for the next accept.
    /// </summary>
    public void Enqueue(IDuplexConnection connection)
    {
        if (!_pending.Writer.TryWrite(connection))
        {
            throw new InvalidOperationException("The test listener has been disposed.");
        }
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _bindCount);

        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _isDisposed = true;
        _pending.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    protected override async ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(TestConnectionListener));
        }
    }
}
