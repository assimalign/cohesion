using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;

namespace GatewaySmoke.Web;

/// <summary>Records the process after the Web host has started all services.</summary>
public sealed class ProcessMarker : IHostLifecycleService
{
    private static int _startedProcessId;
    private static int _startedCount;
    private static int _healthCheckCount;

    /// <summary>Gets the process identifier observed by the member entry point.</summary>
    public static int StartedProcessId => Volatile.Read(ref _startedProcessId);

    /// <summary>Gets the number of Web host generations that reached the started phase.</summary>
    public static int StartedCount => Volatile.Read(ref _startedCount);

    /// <summary>
    /// Gets whether startup and readiness both succeeded for the restarted Web generation.
    /// </summary>
    public static bool RestartReadinessObserved => Volatile.Read(ref _healthCheckCount) >= 7;

    /// <summary>
    /// Reports two healthy startup/readiness checks, exactly three unhealthy first-generation
    /// liveness checks, then remains healthy for the restarted generation.
    /// </summary>
    public static ValueTask<HealthContribution> CheckRestartHealth(
        CancellationToken cancellationToken = default)
    {
        int check = Interlocked.Increment(ref _healthCheckCount);
        HealthContribution contribution = check is >= 3 and <= 5
            ? HealthContribution.Unhealthy($"intentional restart failure {check - 2}/3")
            : HealthContribution.Healthy($"restart smoke check {check}");
        return ValueTask.FromResult(contribution);
    }

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
        Interlocked.Increment(ref _startedCount);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
