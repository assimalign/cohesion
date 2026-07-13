using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// A composed database application: the servers and engines it holds (navigable
/// through <see cref="Context"/>) and the lifecycle that integrates them with the
/// Cohesion hosting model. Built from an <see cref="IDatabaseApplicationBuilder"/>.
/// </summary>
/// <remarks>
/// The application starts and stops its <em>servers</em> (and any additional
/// composed services); engines have no lifecycle to drive — they are data machines,
/// operational from creation and terminal on disposal, owned by whichever
/// composition root created them. This is the Web area's application shape
/// (<c>IWebApplication</c>): a context plus start/stop.
/// </remarks>
public interface IDatabaseApplication
{
    /// <summary>
    /// Gets the composed state of the application: its servers and its server-less
    /// engine registrations.
    /// </summary>
    IDatabaseApplicationContext Context { get; }

    /// <summary>
    /// Starts the application: any composed services first, then the wire-protocol
    /// servers last.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the application in reverse order: the servers drain first, then the
    /// remaining composed services stop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
