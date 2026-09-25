using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.NotificationHub;

/// <summary>
/// Represents the NotificationHub application lifecycle without hosting dependencies.
/// </summary>
public interface INotificationHubApplication
{
    /// <summary>
    /// Gets the application context.
    /// </summary>
    INotificationHubApplicationContext Context { get; }

    /// <summary>
    /// Starts the application.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels application startup.</param>
    /// <returns>A task that completes when the application has started.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the application and drains its active work.
    /// </summary>
    /// <param name="cancellationToken">The token that bounds graceful shutdown.</param>
    /// <returns>A task that completes when the application has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
