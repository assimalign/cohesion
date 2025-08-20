using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

using Cohesion.Internal;

/// <summary>
/// The default host options.
/// </summary>
public abstract class HostOptions<TContext> where TContext : HostContext
{
    private readonly List<Action<TContext>> _traces;

    protected HostOptions()
    {
        _traces = new List<Action<TContext>>();
    }

    /// <summary>
    /// Specify whether the services should be started concurrently.
    /// </summary>
    public bool StartServicesConcurrently { get; set; }

    /// <summary>
    /// Specify the timeout for each service startup. Default is 0.
    /// </summary>
    public TimeSpan? ServiceStartupTimeout { get; set; }

    /// <summary>
    /// The allotted time given for shutdown before forced shutdown.
    /// </summary>
    public TimeSpan? ServiceShutdownTimeout { get; set; }

    /// <summary>
    /// Sets the environment name of the Host. The environment name can be set manually. Otherwise it is set via environment variables within the process.
    /// </summary>
    public string? Environment { get; set; } = AppEnvironment.GetEnvironmentName();

    /// <summary>
    /// Sets a trace handler.
    /// </summary>
    /// <param name="trace"></param>
    public void OnTrace(Action<TContext> trace)
    {
        _traces.Add(ThrowHelper.ThrowIfNull(trace));
    }

    internal IEnumerable<Action<TContext>> GetTraces()
    {
        return _traces;
    }
}
