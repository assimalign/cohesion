using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Represents one configurable execution of a Cohesion host.
/// </summary>
public interface IHostRun
{
    /// <summary>
    /// Gets the host executed by this run.
    /// </summary>
    IHost Host { get; }

    /// <summary>
    /// Gets or sets the host's graceful shutdown timeout, beginning with this run.
    /// </summary>
    TimeSpan ShutdownTimeout { get; set; }

    /// <summary>
    /// Executes the host and reports its run transitions to an optional observer.
    /// </summary>
    /// <param name="observer">The run transition observer, or null for an unobserved run.</param>
    /// <param name="cancellationToken">Signals a shutdown request for this run.</param>
    /// <returns>A task that represents the complete host run.</returns>
    /// <exception cref="InvalidOperationException">This one-shot handle has already been run.</exception>
    /// <remarks>Observer failures are rethrown after coordinated host teardown completes.</remarks>
    Task RunAsync(
        IHostRunObserver? observer,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to request shutdown while this run's host is in the Started state.
    /// </summary>
    /// <param name="onAccepted">
    /// An optional callback invoked under the host state lock when the request is accepted.
    /// </param>
    /// <returns>True when shutdown was accepted; otherwise, false.</returns>
    bool TryShutdown(Action? onAccepted = null);
}
