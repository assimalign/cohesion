using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Provides configuration key/values for an application.
/// </summary>
public interface IConfigurationProvider : IDisposable
{
    /// <summary>
    /// Returns the provider name.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// Get the configuration.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    object Get(Key key);
    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    void Set(Key key, object value);
    /// <summary>
    /// Loads configuration values from the source represented by this <see cref="IConfigurationProvider"/>.
    /// </summary>
    void Load();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task LoadAsync();
    /// <summary>
    /// 
    /// </summary>
    void Refresh();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task RefreshAsync();
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationEntry> EnumerateEntries();


    /// <summary>
    /// Returns the immediate descendant configuration keys for a given parent path based on this
    /// <see cref="IConfigurationProvider"/>s data and the set of keys returned by all the preceding
    /// <see cref="IConfigurationProvider"/>s.
    /// </summary>
    /// <param name="earlierKeys">The child keys returned by the preceding providers for the same parent path.</param>
    /// <param name="parentPath">The parent path.</param>
    /// <returns>The child keys.</returns>
    //IEnumerable<CKey> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath);
}