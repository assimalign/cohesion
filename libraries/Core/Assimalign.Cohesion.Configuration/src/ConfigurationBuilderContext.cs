using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

public sealed class ConfigurationBuilderContext : IConfigurationBuilderContext
{
    private readonly Dictionary<string, object> _properties;
    private readonly Dictionary<string, object>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<IConfigurationProvider> _providers;
    
    internal ConfigurationBuilderContext(List<IConfigurationProvider> providers)
    {
        _properties ??= new Dictionary<string, object>();
        _lookup = _properties.GetAlternateLookup<ReadOnlySpan<char>>();
        _providers = providers;
    }

    public TimeSpan Timeout { get; }
    public Dictionary<string, object> Properties => _properties;
    IDictionary<string, object> IConfigurationBuilderContext.Properties => Properties;
    public IEnumerable<IConfigurationProvider> Providers => _providers.AsReadOnly();
    public T? GetProperty<T>(string key)
    {
        var span = key.AsSpan();

        if (_lookup.TryGetValue(span, out var value) && value is T type)
        {
            return type;
        }

        return default!;
    }
    public bool HasProvider(string name)
    {
        return _providers.Exists(p => p.Name == name);
    }
    public void AddProvider(IConfigurationProvider provider) => _providers.Add(provider);
}
