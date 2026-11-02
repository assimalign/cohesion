using System;
using System.Threading;

using Assimalign.Cohesion.Hosting.Internal;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// The default host options.
/// </summary>
public abstract class HostOptions<TContext> where TContext : HostContext
{
    protected HostOptions()
    {
        
    }

    /// <summary>
    /// Specify whether the services should be started concurrently.
    /// </summary>
    public bool StartServicesConcurrently { get; set; }

    /// <summary>
    /// Specify whether the services should be stopped concurrently.
    /// </summary>
    public bool StopServicesConcurrently { get; set; }

    /// <summary>
    /// Specify the timeout for each service startup. Default is infinite.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets or sets the allotted time given for shutdown before forced shutdown. The default
    /// for an ordinary host is 30 seconds.
    /// </summary>
    /// <remarks>
    /// A shutdown signal drains a host run within this budget, so keep it below the platform's
    /// grace period, the time between its SIGTERM and its SIGKILL (30 seconds by default on
    /// Kubernetes).
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sets the environment name of the Host. The environment name can be set manually. Otherwise it is set via environment variables within the process.
    /// </summary>
    public string? Environment { get; set; } = AppEnvironment.GetEnvironmentName();

    // Registers the process's shutdown signals for one RunAsync; tests replace it to raise a signal.
    internal Func<Action, IDisposable> ShutdownSignals { get; set; } = HostShutdownSignals.Register;
}
