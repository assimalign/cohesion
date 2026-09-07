using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IoTHub;

/// <summary>
/// Defines the contract-only composition seam for an IoT hub application.
/// </summary>
public interface IIoTHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the IoT hub application.
    /// </summary>
    /// <returns>The configured IoT hub application.</returns>
    new IIoTHubApplication Build();
}
