using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Defines the contract-only composition seam for an identity hub application.
/// </summary>
public interface IIdentityHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    new IIdentityHubApplication Build();
}
