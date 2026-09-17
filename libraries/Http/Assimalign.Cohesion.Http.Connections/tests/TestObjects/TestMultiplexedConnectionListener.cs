using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="MultiplexedConnectionListener"/> double that yields queued
/// multiplexed connections. When the queue is empty, <see cref="AcceptAsync"/>
/// waits until a connection is enqueued or the accept is cancelled, so the
/// <see cref="HttpConnectionListener"/> accept loop can keep re-arming.
/// </summary>
internal sealed class TestMultiplexedConnectionListener : MultiplexedConnectionListener
{
    private readonly Queue<MultiplexedConnection> _connections = new();
    private readonly Queue<TaskCompletionSource<MultiplexedConnection>> _waiters = new();
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<object?> _waitingForConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConnectionCapabilities _capabilities;
    private EndPoint _endPoint = new IPEndPoint(IPAddress.Loopback, 16000);
    private int _acceptCount;

    public TestMultiplexedConnectionListener(params MultiplexedConnection[] connections)
        : this(capabilities: null, connections)
    {
    }

    public TestMultiplexedConnectionListener(ConnectionCapabilities? capabilities, params MultiplexedConnection[] connections)
    {
        _capabilities = capabilities ?? TestConnection.DefaultCapabilities with
        {
            IsMultiplexed = true,
            Security = ConnectionSecurity.Tls
        };

        foreach (MultiplexedConnection connection in connections)
        {
            _connections.Enqueue(connection);
        }
    }

    public bool IsDisposed { get; private set; }

    public int BindCount { get; private set; }

    public int AcceptCount => Volatile.Read(ref _acceptCount);

    public Task WaitingForConnection => _waitingForConnection.Task;

    public EndPoint? EndPointAfterBind { get; set; }

    public override EndPoint EndPoint => _endPoint;

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        BindCount++;
        _endPoint = EndPointAfterBind ?? _endPoint;
        return ValueTask.CompletedTask;
    }

    public void Enqueue(MultiplexedConnection connection)
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

    public override ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _acceptCount);

            if (_connections.Count > 0)
            {
                return ValueTask.FromResult(_connections.Dequeue());
            }

            TaskCompletionSource<MultiplexedConnection> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state =>
                {
                    TaskCompletionSource<MultiplexedConnection> completion = (TaskCompletionSource<MultiplexedConnection>)state!;
                    completion.TrySetCanceled();
                }, waiter);
            }

            _waiters.Enqueue(waiter);
            _waitingForConnection.TrySetResult(null);

            return new ValueTask<MultiplexedConnection>(waiter.Task);
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
