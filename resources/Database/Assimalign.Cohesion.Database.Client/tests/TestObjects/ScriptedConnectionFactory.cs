using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// A connection factory whose dial a test scripts: it either throws a given exception or, with
/// none, signals that the dial started and waits for the caller's cancellation.
/// </summary>
internal sealed class ScriptedConnectionFactory : IConnectionFactory
{
    private readonly Func<Exception>? _failure;
    private readonly TaskCompletionSource _dialing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dials;

    private ScriptedConnectionFactory(Func<Exception>? failure)
    {
        _failure = failure;
    }

    /// <summary>Creates a factory whose every dial throws the exception <paramref name="failure"/> creates.</summary>
    public static ScriptedConnectionFactory Failing(Func<Exception> failure) => new(failure);

    /// <summary>Creates a factory whose dial never completes until the caller cancels it.</summary>
    public static ScriptedConnectionFactory Stalled() => new(null);

    public ConnectionCapabilities Capabilities { get; } = new(
        ConnectionProtocol.Tcp,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <summary>Gets a task that completes when the first dial starts.</summary>
    public Task Dialing => _dialing.Task;

    /// <summary>Gets the number of dials started.</summary>
    public int Dials => Volatile.Read(ref _dials);

    public async ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _dials);
        _dialing.TrySetResult();

        if (_failure is not null)
        {
            throw _failure();
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The stalled dial ended without a cancellation.");
    }
}
