using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// The default host options.
/// </summary>
public sealed class HostOptions
{
    /// <summary>
    /// Specify whether the services should be started concurrently.
    /// </summary>
    public bool StartServicesConcurrently { get; set; }
    /// <summary>
    /// Specify the timeout for each service startup. Default is 0.
    /// </summary>
    public TimeSpan? ServiceStartupTimeout { get; set; }
    /// <summary>
    /// 
    /// </summary>
    public TimeSpan? ServiceShutdownTimeout { get; set; }
    /// <summary>
    /// Sets the environment name of
    /// </summary>
    /// <remarks>
    /// The environment name can be set manually. Otherwise it is set via environment variables within the process.
    /// </remarks>
    public string? Environment { get; set; } = AppEnvironment.GetEnvironmentName();

    internal Action<IHostContext> Trace { get; set; } = trace => { };
    /// <summary>
    /// Sets a trace handler.
    /// </summary>
    /// <param name="action"></param>
    public void OnTrace(Action<IHostContext> action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        Trace = action;
    }

    public void OnTrace<T>(Action<T, HostTrace> action)
    {

    }
}
