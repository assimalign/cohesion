using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

/// <summary>
/// A <see cref="ConnectionListener"/> double that yields queued connections.
/// When the queue is empty, <see cref="AcceptAsync"/> waits (like a real
/// listener) until a connection is enqueued or the accept is cancelled, so
/// the <see cref="HttpConnectionListener"/> accept loop can keep re-arming.
/// </summary>
internal sealed class TestConnectionListener : ConnectionListener
{
    private readonly Queue<Connection> _connections = new();
    private readonly Queue<TaskCompletionSource<Connection>> _waiters = new();
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<object?> _waitingForConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConnectionCapabilities _capabilities;

    public TestConnectionListener(params Connection[] connections)
        : this(capabilities: null, connections)
    {
    }

    public TestConnectionListener(ConnectionCapabilities? capabilities, params Connection[] connections)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities;

        foreach (Connection connection in connections)
        {
            _connections.Enqueue(connection);
        }
    }

    public bool IsDisposed { get; private set; }

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

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 15000);

    public override ConnectionCapabilities Capabilities => _capabilities;

    public void Enqueue(Connection connection)
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

    public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            AcceptCount++;

            if (_connections.Count > 0)
            {
                return ValueTask.FromResult(_connections.Dequeue());
            }

            TaskCompletionSource<Connection> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state =>
                {
                    TaskCompletionSource<Connection> completion = (TaskCompletionSource<Connection>)state!;
                    completion.TrySetCanceled();
                }, waiter);
            }

            _waiters.Enqueue(waiter);
            _waitingForConnection.TrySetResult(null);

            return new ValueTask<Connection>(waiter.Task);
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
