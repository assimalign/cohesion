using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MediaHub;

/// <summary>
/// Defines the contract-only composition seam for a media hub application.
/// </summary>
public interface IMediaHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the media hub application.
    /// </summary>
    /// <returns>The configured media hub application.</returns>
    new IMediaHubApplication Build();
}
