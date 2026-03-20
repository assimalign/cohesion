using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Defines a mutable configuration root that can load additional providers at runtime.
/// </summary>
public interface IConfigurationManager : IConfiguration
{
    /// <summary>
    /// Loads and adds the specified provider to the current configuration view.
    /// </summary>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The current manager instance.</returns>
    IConfigurationManager AddProvider(IConfigurationProvider provider);

    /// <summary>
    /// Creates, loads, and adds a provider to the current configuration view.
    /// </summary>
    /// <param name="provider">The provider factory.</param>
    /// <returns>The current manager instance.</returns>
    IConfigurationManager AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider);

    /// <summary>
    /// Asynchronously creates, loads, and adds a provider to the current configuration view.
    /// </summary>
    /// <param name="provider">The asynchronous provider factory.</param>
    /// <param name="cancellationToken">The cancellation token for the registration operation.</param>
    /// <returns>A task that resolves to the current manager instance.</returns>
    ValueTask<IConfigurationManager> AddProviderAsync(
        Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> provider,
        CancellationToken cancellationToken = default);
}
