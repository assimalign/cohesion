using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NotificationHub;

/// <summary>
/// Defines the contract-only composition seam for a notification hub application.
/// </summary>
public interface INotificationHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the notification hub application.
    /// </summary>
    /// <returns>The configured notification hub application.</returns>
    new INotificationHubApplication Build();
}
