using System.IO;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Describes the IdentityHub application context without hosting dependencies.
/// </summary>
public interface IIdentityHubApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
