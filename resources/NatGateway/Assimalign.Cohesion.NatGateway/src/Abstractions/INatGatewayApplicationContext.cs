using System.IO;

namespace Assimalign.Cohesion.NatGateway;

/// <summary>
/// Describes the NatGateway application context without hosting dependencies.
/// </summary>
public interface INatGatewayApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
