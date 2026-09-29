using System.IO;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Describes the SecretStore application context without hosting dependencies.
/// </summary>
public interface ISecretStoreApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
