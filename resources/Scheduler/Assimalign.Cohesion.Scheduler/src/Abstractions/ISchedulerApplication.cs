using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Represents a scheduler application and its host lifecycle.
/// </summary>
public interface ISchedulerApplication : IHost
{
    /// <summary>
    /// Gets the scheduler-specific application context.
    /// </summary>
    new ISchedulerApplicationContext Context { get; }

    /// <summary>
    /// Runs the application until shutdown is requested.
    /// </summary>
    /// <param name="cancellationToken">The token that requests application shutdown.</param>
    /// <returns>A task that completes after the application has stopped.</returns>
    /// <exception cref="System.ObjectDisposedException">The application has been disposed.</exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}
