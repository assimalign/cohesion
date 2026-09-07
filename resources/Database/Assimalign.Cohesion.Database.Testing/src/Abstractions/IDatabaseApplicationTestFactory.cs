using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Drives one Cohesion database resource program in process under an isolated
/// <see cref="ResourceContext"/>.
/// </summary>
/// <remarks>
/// The factory invokes the resource's real entry point, waits for its generated Database
/// control plane to report readiness, and requests graceful shutdown through that same
/// control plane. Factories do not mutate process-wide environment variables and may run in
/// parallel when their contexts use distinct endpoints and mounts.
/// </remarks>
public interface IDatabaseApplicationTestFactory : IAsyncDisposable
{
    /// <summary>Gets the ambient resource context installed while the program runs.</summary>
    ResourceContext ResourceContext { get; }

    /// <summary>Gets a value indicating whether the resource has passed its readiness probe.</summary>
    bool IsStarted { get; }

    /// <summary>
    /// Invokes the resource program and waits for its Database control plane to report ready.
    /// Repeated calls after readiness are no-ops.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for startup.</param>
    /// <returns>A task that completes when the resource is ready.</returns>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The resource entry point exited before readiness.</exception>
    /// <exception cref="TimeoutException">The resource did not become ready within the configured budget.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests graceful shutdown through the Database control plane and waits for the
    /// resource program to exit. Stopping a factory that has not started is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Bounds the graceful shutdown wait.</param>
    /// <returns>A task that completes when the resource program exits.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
