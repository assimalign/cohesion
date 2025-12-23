using System;
using System.IO;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public readonly struct HostEventArgs
{
    public HostEventArgs(
        HostId id, 
        HostState state,
        IHostEnvironment environment,
        HostEvent @event,
        Exception? exception = null)
    {
        HostId = id;
        State = state;
        Event = @event;
        Environment = environment;
        Exception = exception;
    }

    /// <summary>
    /// The unique identifier for the host.
    /// </summary>
    public HostId HostId { get; }

    /// <summary>
    /// The state of the host running.
    /// </summary>
    public HostState State { get; }

    /// <summary>
    /// The host environment information.
    /// </summary>
    public IHostEnvironment Environment { get; }

    /// <summary>
    /// TGets the host event.
    /// </summary>
    public HostEvent Event { get; }

    /// <summary>
    /// 
    /// </summary>
    public Exception? Exception { get; }
}
