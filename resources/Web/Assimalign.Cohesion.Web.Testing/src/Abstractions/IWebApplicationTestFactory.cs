using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// A per-test host for a Cohesion web application, created either through manual in-memory
/// composition or from a resource executable's real <c>Program</c> entry point.
/// </summary>
/// <remarks>
/// Factories are independent: manual factories own private in-memory transports, while
/// Program-backed factories own invocation-local <c>ResourceRuntime</c> scopes and loopback
/// endpoints. Dispose the factory to stop it and release its transport. A Program-backed factory
/// requests a graceful stop bounded by its shutdown budget; a manual factory cancels the requests
/// still in flight, so call <see cref="StopAsync"/> first when they must finish.
/// </remarks>
public interface IWebApplicationTestFactory : IAsyncDisposable
{
    /// <summary>
    /// Gets the web application under test. Manual factories build it on first access;
    /// Program-backed factories expose it after <see cref="StartAsync"/> or
    /// <see cref="CreateClient"/> has started the executable.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown by a Program-backed factory when the resource Program has not built its host yet.
    /// </exception>
    IWebApplication Application { get; }

    /// <summary>
    /// Gets a value indicating whether the factory's server has been started.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Starts the manual server or the resource Program. Starting an already-started factory
    /// is a no-op.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the start wait. A Program-backed factory does not invoke a pre-cancelled start;
    /// after invocation begins it requests graceful stop as soon as the Program builds its host.
    /// User code that never reaches <c>Build()</c> cannot be preempted in-process.
    /// </param>
    /// <returns>A task that completes when the server reports ready.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> cancels the start wait.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when a Program-backed factory does not report ready within its startup budget.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a Program-backed factory has no registered enabled entry point, builds a
    /// non-Web host, or exits before reporting ready.
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully stops the factory's server or resource Program. Stopping a never-started or
    /// already-stopped factory is a no-op.
    /// </summary>
    /// <remarks>
    /// A factory over the default server drains lame-duck style: it stops accepting and lets requests in
    /// flight finish. When <paramref name="cancellationToken"/> cancels, it cancels the requests still
    /// running, aborts their connections, and completes normally. A Program-backed factory asks the Program
    /// to stop and observes the token while it waits for the Program to exit.
    /// </remarks>
    /// <param name="cancellationToken">The caller's shutdown budget.</param>
    /// <returns>
    /// A task that completes when the server has stopped, drained or cut short by the budget, or when the
    /// Program has exited.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown by a Program-backed factory when <paramref name="cancellationToken"/> cancels the wait for the
    /// Program to exit.
    /// </exception>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> for the hosted application, starting the factory
    /// first when needed. The caller owns the returned client and should dispose it.
    /// </summary>
    /// <returns>A client whose requests are served by the application under test.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when a Program-backed factory does not report ready within its startup budget.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a Program-backed factory has no registered enabled entry point, builds a
    /// non-Web host, or exits before reporting ready.
    /// </exception>
    HttpClient CreateClient();
}
