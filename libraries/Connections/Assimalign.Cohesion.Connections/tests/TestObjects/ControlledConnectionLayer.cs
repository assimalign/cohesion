using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A pass-through <see cref="IConnectionLayer"/> double whose upgrade of each connection the test decides:
/// it completes at once by default, waits while the connection is held (until released or canceled), or
/// throws a planned failure. It signals when each connection's upgrade starts.
/// </summary>
internal sealed class ControlledConnectionLayer : IConnectionLayer
{
    private readonly ConcurrentDictionary<ConnectionId, TaskCompletionSource> _holds = new();
    private readonly ConcurrentDictionary<ConnectionId, Exception> _failures = new();
    private readonly ConcurrentDictionary<ConnectionId, TaskCompletionSource> _started = new();

    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities) => capabilities;

    /// <summary>Makes the connection's upgrade wait until <see cref="Release"/> or until its token is canceled.</summary>
    public void Hold(IConnection connection)
        => _holds[connection.Id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets a held upgrade complete.</summary>
    public void Release(IConnection connection) => _holds[connection.Id].TrySetResult();

    /// <summary>Makes the connection's upgrade throw <paramref name="exception"/>.</summary>
    public void Fail(IConnection connection, Exception exception) => _failures[connection.Id] = exception;

    /// <summary>Completes when the connection's upgrade has started.</summary>
    public Task WhenStarted(IConnection connection) => GetStarted(connection.Id).Task;

    public async ValueTask<IConnection> UpgradeAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        GetStarted(connection.Id).TrySetResult();

        if (_failures.TryGetValue(connection.Id, out Exception? failure))
        {
            throw failure;
        }

        if (_holds.TryGetValue(connection.Id, out TaskCompletionSource? hold))
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return connection;
    }

    private TaskCompletionSource GetStarted(ConnectionId connectionId)
        => _started.GetOrAdd(connectionId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
