namespace Assimalign.Cohesion.NotificationHub;

/// <summary>
/// Defines the contract-only composition seam for a notification hub application.
/// </summary>
public interface INotificationHubApplicationBuilder
{
    /// <summary>
    /// Builds the notification hub application.
    /// </summary>
    /// <returns>The configured notification hub application.</returns>
    INotificationHubApplication Build();
}
