using System.IO;

namespace Assimalign.Cohesion.IoTHub;

/// <summary>
/// Describes the IoTHub application context without hosting dependencies.
/// </summary>
public interface IIoTHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
