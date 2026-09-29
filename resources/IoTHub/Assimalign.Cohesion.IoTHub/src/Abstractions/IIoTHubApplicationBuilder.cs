namespace Assimalign.Cohesion.IoTHub;

/// <summary>
/// Defines the contract-only composition seam for an IoT hub application.
/// </summary>
public interface IIoTHubApplicationBuilder
{
    /// <summary>
    /// Builds the IoT hub application.
    /// </summary>
    /// <returns>The configured IoT hub application.</returns>
    IIoTHubApplication Build();
}
