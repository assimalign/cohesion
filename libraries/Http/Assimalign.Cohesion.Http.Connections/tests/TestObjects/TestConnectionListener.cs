using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="DuplexConnectionListener"/> double that yields queued connections.
/// When the queue is empty, <see cref="AcceptAsync"/> waits (like a real
/// listener) until a connection is enqueued or the accept is cancelled, so
/// the <see cref="HttpConnectionListener"/> accept loop can keep re-arming.
/// </summary>
internal sealed class TestConnectionListener : DuplexConnectionListener
{
    private readonly Queue<IDuplexConnection> _connections = new();
    private readonly Queue<TaskCompletionSource<IDuplexConnection>> _waiters = new();
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<object?> _waitingForConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionListener(params IDuplexConnection[] connections)
        : this(capabilities: null, connections)
    {
    }

    public TestConnectionListener(ConnectionCapabilities? capabilities, params IDuplexConnection[] connections)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;

        foreach (IDuplexConnection connection in connections)
        {
            _connections.Enqueue(connection);
        }
    }

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// The number of times <see cref="BindAsync"/> has been invoked.
    /// </summary>
    public int BindCount { get; private set; }

    /// <summary>
    /// The number of times <see cref="AcceptAsync"/> has been invoked — lets
    /// tests assert the accept loop re-arms after each accepted connection.
    /// </summary>
    public int AcceptCount { get; private set; }

    /// <summary>
    /// Completes the first time an accept has to wait on an empty queue, so
    /// tests can deterministically interleave Enqueue with the accept loop.
    /// </summary>
    public Task WaitingForConnection => _waitingForConnection.Task;

    public override ListenerId Id { get; } = ListenerId.New();

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 15000);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        BindCount++;
        return ValueTask.CompletedTask;
    }

    public void Enqueue(IDuplexConnection connection)
    {
        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                if (_waiters.Dequeue().TrySetResult(connection))
                {
                    return;
                }
            }

            _connections.Enqueue(connection);
        }
    }

    protected override ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            AcceptCount++;

            if (_connections.Count > 0)
            {
                return ValueTask.FromResult(_connections.Dequeue());
            }

            TaskCompletionSource<IDuplexConnection> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state =>
                {
                    TaskCompletionSource<IDuplexConnection> completion = (TaskCompletionSource<IDuplexConnection>)state!;
                    completion.TrySetCanceled();
                }, waiter);
            }

            _waiters.Enqueue(waiter);
            _waitingForConnection.TrySetResult(null);

            return new ValueTask<IDuplexConnection>(waiter.Task);
        }
    }

    public override ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            IsDisposed = true;

            while (_waiters.Count > 0)
            {
                _waiters.Dequeue().TrySetCanceled();
            }
        }

        return ValueTask.CompletedTask;
    }
}
