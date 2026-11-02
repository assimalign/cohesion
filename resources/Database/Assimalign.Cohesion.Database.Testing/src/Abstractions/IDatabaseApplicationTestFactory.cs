using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Testing;

/// <summary>
/// Drives one Cohesion database program in process, inside an <see cref="AppEnvironmentScope"/>
/// of its own.
/// </summary>
/// <remarks>
/// The factory invokes the program's real entry point and stops it by signaling its scope's
/// shutdown, which a host run inside the scope stops on. Factories do not mutate process-wide
/// environment variables and may run in parallel when their programs use distinct endpoints and
/// data paths.
/// </remarks>
public interface IDatabaseApplicationTestFactory : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the program was started and has not been stopped.</summary>
    bool IsStarted { get; }

    /// <summary>
    /// Invokes the program's entry point on a thread of its own. A plain program reports no
    /// readiness, so the call returns once the entry point is launched; the program's failures
    /// surface from <see cref="StopAsync"/>. Repeated calls are no-ops.
    /// </summary>
    /// <param name="cancellationToken">Cancels the start before the program is launched.</param>
    /// <returns>A task that completes when the program is launched.</returns>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the launch.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals the program's shutdown and waits for its entry point to return. Stopping a factory
    /// that has not started is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Bounds the graceful shutdown wait.</param>
    /// <returns>A task that completes when the program's entry point returns.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
