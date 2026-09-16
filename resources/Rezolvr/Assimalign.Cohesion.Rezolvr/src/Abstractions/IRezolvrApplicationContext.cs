using System.IO;

namespace Assimalign.Cohesion.Rezolvr;

/// <summary>
/// Describes the Rezolvr application context without hosting dependencies.
/// </summary>
public interface IRezolvrApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
