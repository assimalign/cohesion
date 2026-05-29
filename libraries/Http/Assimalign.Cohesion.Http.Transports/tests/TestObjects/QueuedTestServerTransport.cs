using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class QueuedTestServerTransport : ServerTransport
{
    private readonly Queue<TransportConnection> _connections;
    private readonly Queue<TaskCompletionSource<TransportConnection>> _waiters;
    private readonly Lock _lock;
    private readonly TaskCompletionSource<object?> _waitingForConnection;

    public QueuedTestServerTransport(TransportProtocol protocol)
    {
        _connections = new Queue<TransportConnection>();
        _waiters = new Queue<TaskCompletionSource<TransportConnection>>();
        _lock = new Lock();
        _waitingForConnection = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Protocol = protocol;
    }

    public override TransportProtocol Protocol { get; }

    public int InitializeAsyncCount { get; private set; }

    public Task WaitingForConnection => _waitingForConnection.Task;

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    protected override Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            InitializeAsyncCount++;

            if (_connections.Count > 0)
            {
                return Task.FromResult(_connections.Dequeue());
            }

            TaskCompletionSource<TransportConnection> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state =>
                {
                    TaskCompletionSource<TransportConnection> completion = (TaskCompletionSource<TransportConnection>)state!;
                    completion.TrySetCanceled();
                }, waiter);
            }

            _waiters.Enqueue(waiter);
            _waitingForConnection.TrySetResult(null);

            return waiter.Task;
        }
    }

    public void Enqueue(TransportConnection connection)
    {
        lock (_lock)
        {
            if (_waiters.Count > 0)
            {
                _waiters.Dequeue().TrySetResult(connection);
                return;
            }

            _connections.Enqueue(connection);
        }
    }
}
