using System.IO;

namespace Assimalign.Cohesion.VpnGateway;

/// <summary>
/// Describes the VpnGateway application context without hosting dependencies.
/// </summary>
public interface IVpnGatewayApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}
