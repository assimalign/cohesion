using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Defines the contract-only composition seam for a secret store application.
/// </summary>
public interface ISecretStoreApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the secret store application.
    /// </summary>
    /// <returns>The configured secret store application.</returns>
    new ISecretStoreApplication Build();
}
