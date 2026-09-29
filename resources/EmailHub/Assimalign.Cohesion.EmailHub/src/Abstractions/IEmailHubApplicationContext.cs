using System.IO;

namespace Assimalign.Cohesion.EmailHub;

/// <summary>
/// Describes the EmailHub application context without hosting dependencies.
/// </summary>
public interface IEmailHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
