using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the shared state available while configuration providers are being registered.
/// </summary>
public sealed class ConfigurationBuilderContext : IConfigurationBuilderContext
{
    private readonly Dictionary<string, object> _properties;
    private readonly Dictionary<string, object>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<IConfigurationProvider> _providers;

    internal ConfigurationBuilderContext(TimeSpan timeout, IEnumerable<IConfigurationProvider>? providers = null)
    {
        Timeout = timeout;
        _properties = new Dictionary<string, object>(StringComparer.Ordinal);
        _lookup = _properties.GetAlternateLookup<ReadOnlySpan<char>>();
        _providers = providers is null ? [] : new List<IConfigurationProvider>(providers);
    }

    /// <inheritdoc />
    public TimeSpan Timeout { get; }

    /// <inheritdoc />
    public IDictionary<string, object> Properties => _properties;

    /// <inheritdoc />
    public IEnumerable<IConfigurationProvider> Providers => _providers.AsReadOnly();

    internal bool TryGetProperty<T>(ReadOnlySpan<char> key, out T? value)
    {
        if (_lookup.TryGetValue(key, out object? candidate) && candidate is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    internal bool HasProvider(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return _providers.Exists(provider => string.Equals(provider.Name, name, StringComparison.Ordinal));
    }

    internal void AddProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _providers.Add(provider);
    }
}
