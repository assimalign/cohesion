using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApiManager;

/// <summary>
/// Defines the contract-only composition seam for an API manager application.
/// </summary>
public interface IApiManagerApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the API manager application.
    /// </summary>
    /// <returns>The configured API manager application.</returns>
    new IApiManagerApplication Build();
}
