using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EventHub;

/// <summary>
/// Defines the contract-only composition seam for an event hub application.
/// </summary>
public interface IEventHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the event hub application.
    /// </summary>
    /// <returns>The configured event hub application.</returns>
    new IEventHubApplication Build();
}
