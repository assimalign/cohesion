namespace Assimalign.Cohesion.MediaHub;

/// <summary>
/// Defines the contract-only composition seam for a media hub application.
/// </summary>
public interface IMediaHubApplicationBuilder
{
    /// <summary>
    /// Builds the media hub application.
    /// </summary>
    /// <returns>The configured media hub application.</returns>
    IMediaHubApplication Build();
}
