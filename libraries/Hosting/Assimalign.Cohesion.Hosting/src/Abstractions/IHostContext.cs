using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Host Information.
/// </summary>
public interface IHostContext
{
    /// <summary>
    /// The unique identifier for the host.
    /// </summary>
    HostId HostId { get; }

    /// <summary>
    /// The state of the host running.
    /// </summary>
    HostState State { get; }

    /// <summary>
    /// The host environment information.
    /// </summary>
    IHostEnvironment Environment { get; }

    /// <summary>
    /// A collection of hosted services.
    /// </summary>
    IEnumerable<IHostService> HostedServices { get; }

    /// <summary>
    /// Signals the host to shutdown
    /// </summary>
    void Shutdown();
}