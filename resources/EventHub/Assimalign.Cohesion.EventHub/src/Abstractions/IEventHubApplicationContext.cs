using System.IO;

namespace Assimalign.Cohesion.EventHub;

/// <summary>
/// Describes the EventHub application context without hosting dependencies.
/// </summary>
public interface IEventHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
