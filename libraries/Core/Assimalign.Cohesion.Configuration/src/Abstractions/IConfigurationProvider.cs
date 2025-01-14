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
    //IConfigurationEntry? this[Key key] { get; }
    /// <summary>
    /// Get the configuration.
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