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
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationEntry? this[Key key] { get; }
    /// <summary>
    /// Get the configuration.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    object Get(Key key);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="entry"></param>
    /// <returns></returns>
    bool TryGet(Key key, out IConfigurationEntry? entry);
    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    void Set(Key key, object value);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TrySet(Key key, object value);
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
    IEnumerable<IConfigurationEntry> GetEntries();
}