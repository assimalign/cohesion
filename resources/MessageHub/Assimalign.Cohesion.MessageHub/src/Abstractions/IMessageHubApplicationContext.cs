using System.IO;

namespace Assimalign.Cohesion.MessageHub;

/// <summary>
/// Describes the MessageHub application context without hosting dependencies.
/// </summary>
public interface IMessageHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
