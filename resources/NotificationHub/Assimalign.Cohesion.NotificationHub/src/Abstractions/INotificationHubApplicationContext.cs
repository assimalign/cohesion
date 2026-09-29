using System.IO;

namespace Assimalign.Cohesion.NotificationHub;

/// <summary>
/// Describes the NotificationHub application context without hosting dependencies.
/// </summary>
public interface INotificationHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
