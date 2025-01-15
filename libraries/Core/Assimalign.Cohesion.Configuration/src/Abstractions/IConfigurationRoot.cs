using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the root of an <see cref="IConfiguration"/> hierarchy.
/// </summary>
public interface IConfigurationRoot : IConfiguration, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The <see cref="IConfigurationProvider"/>s for this configuration.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
    /// <summary>
    /// Gets the named configuration provider.
    /// </summary>
    /// <param name="name">The <see cref="IConfigurationProvider.Name"/>.</param>
    /// <returns></returns>
    IConfigurationProvider GetProvider(string name);
    /// <summary>
    /// Force the configuration values to be reloaded from the underlying <see cref="IConfigurationProvider"/>s.
    /// </summary>
    void Reload();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task ReloadAsync();
}