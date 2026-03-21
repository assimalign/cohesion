using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

internal sealed class QueuedTestServerTransport : ITransport
{
    private readonly Queue<ITransportConnection> _connections;
    private readonly Queue<TaskCompletionSource<ITransportConnection>> _waiters;
    private readonly Lock _lock;
    private readonly TaskCompletionSource<object?> _waitingForConnection;

    public QueuedTestServerTransport(TransportProtocol protocol)
    {
        _connections = new Queue<ITransportConnection>();
        _waiters = new Queue<TaskCompletionSource<ITransportConnection>>();
        _lock = new Lock();
        _waitingForConnection = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Protocol = protocol;
        Id = TransportId.New();
    }

    public TransportId Id { get; }

    public TransportKind Kind => TransportKind.Server;

    public TransportProtocol Protocol { get; }

    public int InitializeAsyncCount { get; private set; }

    public Task WaitingForConnection => _waitingForConnection.Task;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ITransportConnection Initialize()
    {
        return InitializeAsync().GetAwaiter().GetResult();
    }

    public Task<ITransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            InitializeAsyncCount++;

            if (_connections.Count > 0)
            {
                return Task.FromResult(_connections.Dequeue());
            }

            TaskCompletionSource<ITransportConnection> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state =>
                {
                    TaskCompletionSource<ITransportConnection> completion = (TaskCompletionSource<ITransportConnection>)state!;
                    completion.TrySetCanceled();
                }, waiter);
            }

            _waiters.Enqueue(waiter);
            _waitingForConnection.TrySetResult(null);

            return waiter.Task;
        }
    }

    public void Enqueue(ITransportConnection connection)
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
