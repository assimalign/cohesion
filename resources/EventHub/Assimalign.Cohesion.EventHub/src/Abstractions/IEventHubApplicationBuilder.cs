namespace Assimalign.Cohesion.EventHub;

/// <summary>
/// Defines the contract-only composition seam for an event hub application.
/// </summary>
public interface IEventHubApplicationBuilder
{
    /// <summary>
    /// Builds the event hub application.
    /// </summary>
    /// <returns>The configured event hub application.</returns>
    IEventHubApplication Build();
}
