using System.IO;

namespace Assimalign.Cohesion.ConfigurationStore;

/// <summary>
/// Describes the ConfigurationStore application context without hosting dependencies.
/// </summary>
public interface IConfigurationStoreApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
