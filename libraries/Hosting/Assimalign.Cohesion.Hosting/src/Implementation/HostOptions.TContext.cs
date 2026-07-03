using System;
using System.Threading;
using System.Collections.Generic;

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
    /// The allotted time given for shutdown before forced shutdown. Default is 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sets the environment name of the Host. The environment name can be set manually. Otherwise it is set via environment variables within the process.
    /// </summary>
    public string? Environment { get; set; } = AppEnvironment.GetEnvironmentName();
}
