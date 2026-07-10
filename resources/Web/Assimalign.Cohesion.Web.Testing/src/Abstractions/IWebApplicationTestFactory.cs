using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// A per-test host for a Cohesion web application: it boots the application against an
/// in-memory connection listener — no sockets, no ports — manages the server's lifecycle,
/// and hands out <see cref="HttpClient"/> instances whose requests flow the application's
/// full pipeline (middleware, routing, features) end to end.
/// </summary>
/// <remarks>
/// Factories are independent: multiple factories in one process share no listener, router,
/// or pipeline state, so tests can run in parallel. Dispose the factory when the test
/// completes; disposal stops the server (draining in-flight connections) and tears down the
/// in-memory transport.
/// </remarks>
public interface IWebApplicationTestFactory : IAsyncDisposable
{
    /// <summary>
    /// Gets the web application under test. Accessing this member builds the application if
    /// it has not been built yet; configure the request pipeline on it before the factory
    /// starts serving.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    IWebApplication Application { get; }

    /// <summary>
    /// Gets a value indicating whether the factory's server has been started.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Builds the application if needed and starts its server against the in-memory
    /// listener. Starting an already-started factory is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Aborts the start if signaled.</param>
    /// <returns>A task that completes when the server is accepting in-memory connections.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the factory's server: no further connections are accepted and in-flight
    /// connections are drained. Stopping a never-started or already-stopped factory is a
    /// no-op.
    /// </summary>
    /// <param name="cancellationToken">The caller's shutdown budget.</param>
    /// <returns>A task that completes when the server has fully drained.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> that dials the factory's in-memory listener,
    /// starting the factory first when it is not started yet. The caller owns the returned
    /// client and should dispose it.
    /// </summary>
    /// <returns>A client whose requests are served by the application under test.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    HttpClient CreateClient();
}
