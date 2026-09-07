using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EventHub;

/// <summary>
/// Represents an event hub application and its host lifecycle.
/// </summary>
public interface IEventHubApplication : IHost
{
    /// <summary>
    /// Runs the application until shutdown is requested.
    /// </summary>
    /// <param name="cancellationToken">The token that requests application shutdown.</param>
    /// <returns>A task that completes after the application has stopped.</returns>
    /// <exception cref="System.ObjectDisposedException">The application has been disposed.</exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}
