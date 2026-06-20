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
    private readonly ConnectionCapabilities _capabilities;

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

    public override EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 16000);

    public override ConnectionCapabilities Capabilities => _capabilities;

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
