using System.IO;

namespace Assimalign.Cohesion.MediaHub;

/// <summary>
/// Describes the MediaHub application context without hosting dependencies.
/// </summary>
public interface IMediaHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
