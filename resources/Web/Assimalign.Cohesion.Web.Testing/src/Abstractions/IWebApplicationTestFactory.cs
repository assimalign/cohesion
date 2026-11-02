using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// A per-test host for a Cohesion web application, composed in memory.
/// </summary>
/// <remarks>
/// Factories are independent: each owns a private in-memory transport. Dispose the factory to stop
/// it and release its transport. Disposal cancels the requests still in flight, so call
/// <see cref="StopAsync"/> first when they must finish.
/// </remarks>
public interface IWebApplicationTestFactory : IAsyncDisposable
{
    /// <summary>
    /// Gets the web application under test; the factory builds it on first access.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    IWebApplication Application { get; }

    /// <summary>
    /// Gets a value indicating whether the factory's server has been started.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Starts the factory's server. Starting an already-started factory is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start wait.</param>
    /// <returns>A task that completes when the server reports ready.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> cancels the start wait.
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully stops the factory's server. Stopping a never-started or already-stopped factory
    /// is a no-op.
    /// </summary>
    /// <remarks>
    /// A factory over the default server drains lame-duck style: it stops accepting and lets requests in
    /// flight finish. When <paramref name="cancellationToken"/> cancels, it cancels the requests still
    /// running, aborts their connections, and completes normally.
    /// </remarks>
    /// <param name="cancellationToken">The caller's shutdown budget.</param>
    /// <returns>A task that completes when the server has stopped, drained or cut short by the budget.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> for the hosted application, starting the factory
    /// first when needed. The caller owns the returned client and should dispose it.
    /// </summary>
    /// <returns>A client whose requests are served by the application under test.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    HttpClient CreateClient();
}
