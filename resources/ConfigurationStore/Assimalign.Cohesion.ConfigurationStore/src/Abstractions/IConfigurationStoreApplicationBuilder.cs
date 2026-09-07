using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore;

/// <summary>
/// Defines the contract-only composition seam for a configuration store application.
/// </summary>
public interface IConfigurationStoreApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the configuration store application.
    /// </summary>
    /// <returns>The configured configuration store application.</returns>
    new IConfigurationStoreApplication Build();
}
