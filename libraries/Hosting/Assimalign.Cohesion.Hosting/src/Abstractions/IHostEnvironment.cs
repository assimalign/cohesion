using System.IO;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IHostEnvironment
{
    /// <summary>
    /// Gets the name of the environment.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Gets the absolute path to the directory that contains the application content files.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}