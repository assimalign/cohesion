using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
    /// Waits until shutdown is requested for the current host lifetime.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait without stopping the host.</param>
    /// <returns>
    /// A task that completes when shutdown is requested or the host begins stopping, stops,
    /// or fails.
    /// </returns>
    Task WaitForShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals the host to shutdown
    /// </summary>
    void Shutdown();
}
