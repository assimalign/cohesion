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
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    string? Get(Path path);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGetValue(Path path, out string value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    void Set(Path path, string? value);

    /// <summary>
    /// Checks whether the key exists.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool ContainsPath(Path path);

    /// <summary>
    /// Get the configuration entry, if exists.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationEntry? Get(Key key);

    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="entry">The value.</param>
    void Set(IConfigurationEntry entry);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="IConfigurationEntry"></param>
    void Remove(IConfigurationEntry entry);

    

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
    /// Returns a collection of entries from the provider.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationEntry> GetEntries();
}