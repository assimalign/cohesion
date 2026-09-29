using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A listener whose bind completion is controlled by a test, exposing the
/// server's bind/start ordering without relying on transport timing.
/// </summary>
internal sealed class ControlledConnectionListener : ConnectionListener
{
    private readonly TaskCompletionSource<bool> _bindStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _bindReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Connection> _connections = Channel.CreateUnbounded<Connection>();
    private Exception? _bindException;
    private int _acceptCount;

    public override EndPoint EndPoint { get; } = new InMemoryEndPoint("controlled-bind");

    public override ConnectionCapabilities Capabilities => InMemoryConnectionPair.DefaultCapabilities;

    public Task BindStarted => _bindStarted.Task;

    public bool IsDisposed { get; private set; }

    public int AcceptCount => Volatile.Read(ref _acceptCount);

    public void CompleteBind(Exception? exception = null)
    {
        _bindException = exception;
        _bindReleased.TrySetResult(true);
    }

    public override async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        _bindStarted.TrySetResult(true);
        await _bindReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_bindException is not null)
        {
            throw _bindException;
        }
    }

    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acceptCount);
        return await _connections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _bindReleased.TrySetCanceled();
        _connections.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
