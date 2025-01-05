using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

public class ChainedConfigurationProvider : IConfigurationProvider, IDisposable
{
    private readonly IConfiguration configuration;
    private readonly bool disposeConfiguration;

    /// <summary>
    /// Initialize a new instance from the source configuration.
    /// </summary>
    /// <param name="source">The source configuration.</param>
    public ChainedConfigurationProvider(ChainedConfigurationSource source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        if (source.Configuration == null)
        {
            throw new ArgumentException();// SR.Format(SR.InvalidNullArgument, "source.Configuration"), nameof(source));
        }

        configuration = source.Configuration;
        disposeConfiguration = source.ShouldDisposeConfiguration;
    }

    /// <summary>
    /// Tries to get a configuration value for the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns><c>True</c> if a value for the specified key was found, otherwise <c>false</c>.</returns>
    public bool TryGet(string key, out string value)
    {
        value = configuration[key];
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>
    /// Sets a configuration value for the specified key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void Set(string key, string value) => configuration[key] = value;

    /// <summary>
    /// Returns a change token if this provider supports change tracking, null otherwise.
    /// </summary>
    /// <returns>The change token.</returns>
    public IChangeToken GetReloadToken() => configuration.GetReloadToken();

    /// <summary>
    /// Loads configuration values from the source represented by this <see cref="IConfigurationProvider"/>.
    /// </summary>
    public void Load() { }

    /// <summary>
    /// Returns the immediate descendant configuration keys for a given parent path based on this
    /// <see cref="IConfigurationProvider"/>s data and the set of keys returned by all the preceding
    /// <see cref="IConfigurationProvider"/>s.
    /// </summary>
    /// <param name="earlierKeys">The child keys returned by the preceding providers for the same parent path.</param>
    /// <param name="parentPath">The parent path.</param>
    /// <returns>The child keys.</returns>
    public IEnumerable<string> GetChildKeys(
        IEnumerable<string> earlierKeys,
        string parentPath)
    {
        IConfiguration section = parentPath == null ? configuration : configuration.GetSection(parentPath);
        var keys = new List<string>();
        foreach (IConfigurationSection child in section.GetChildren())
        {
            keys.Add(child.Key);
        }
        keys.AddRange(earlierKeys);
        keys.Sort(ConfigurationKeyComparer.Comparison);
        return keys;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposeConfiguration)
        {
            (configuration as IDisposable)?.Dispose();
        }
    }

    public string Get(string key)
    {
        throw new NotImplementedException();
    }
}
