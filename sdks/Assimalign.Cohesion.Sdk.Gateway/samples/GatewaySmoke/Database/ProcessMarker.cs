using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace GatewaySmoke.Database;

/// <summary>Records the process after the Database host has started all services.</summary>
public sealed class ProcessMarker : IHostLifecycleService
{
    private static int _startedProcessId;

    /// <summary>Gets the process identifier observed by the member entry point.</summary>
    public static int StartedProcessId => Volatile.Read(ref _startedProcessId);

    /// <inheritdoc />
    public ServiceId Id { get; } = ServiceId.New();

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _startedProcessId, Environment.ProcessId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
