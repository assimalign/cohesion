using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Provides configuration key/values for an application.
/// </summary>
public interface IConfigurationProvider : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Returns the provider name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Get the configuration entry, if existing.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationEntry? Get(Key key);

    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="entry">The value.</param>
    void Set(IConfigurationEntry? entry);

    /// <summary>
    /// Checks whether the key exists.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool ContainsKey(Key key);

    /// <summary>
    /// Synchronously loads the configuration values.
    /// </summary>
    void Load();

    /// <summary>
    /// Asynchronously loads the configuration values.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously reloads the configuration provider.
    /// </summary>
    void Reload();

    /// <summary>
    /// Asynchronously reloads the configuration provider.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationEntry> GetEntries();
}