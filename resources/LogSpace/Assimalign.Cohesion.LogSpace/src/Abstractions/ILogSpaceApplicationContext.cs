using System.IO;

namespace Assimalign.Cohesion.LogSpace;

/// <summary>
/// Describes the LogSpace application context without hosting dependencies.
/// </summary>
public interface ILogSpaceApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
