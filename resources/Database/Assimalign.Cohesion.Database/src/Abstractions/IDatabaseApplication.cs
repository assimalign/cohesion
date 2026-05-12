using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a host-facing database application that integrates an engine
/// with the Cohesion hosting lifecycle.
/// </summary>
public interface IDatabaseApplication
{
    /// <summary>
    /// Gets the database engine managed by this application.
    /// </summary>
    IDatabaseEngine Engine { get; }

    /// <summary>
    /// Starts the host and initializes runtime dependencies.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the engine and flushes pending state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
