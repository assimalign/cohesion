using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MessageHub;

/// <summary>
/// Defines the contract-only composition seam for a message hub application.
/// </summary>
public interface IMessageHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the message hub application.
    /// </summary>
    /// <returns>The configured message hub application.</returns>
    new IMessageHubApplication Build();
}
