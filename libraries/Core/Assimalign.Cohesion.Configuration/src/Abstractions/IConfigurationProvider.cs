using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Provides configuration key/values for an application.
/// </summary>
public interface IConfigurationProvider
{
    /// <summary>
    /// Returns the provider name.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="path"></param>
    /// <returns></returns>
    T Get<T>(ConfigPath path);
    /// <summary>
    /// Get the configuration 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    object Get(ConfigPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGet<T>(ConfigPath path, out T value);
    /// <summary>
    /// Tries to get a configuration value for the specified key.
    /// </summary>
    /// <param name="path">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns><c>True</c> if a value for the specified key was found, otherwise <c>false</c>.</returns>
    bool TryGet(ConfigPath path, out object value);
    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="path">The key.</param>
    /// <param name="value">The value.</param>
    void Set(ConfigPath path, object value);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TrySet(ConfigPath path, object value);
    /// <summary>
    /// Loads configuration values from the source represented by this <see cref="IConfigurationProvider"/>.
    /// </summary>
    void Load();
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
    IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath);
}