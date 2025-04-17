using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Internal;

internal class ConfigurationContext : IConfigurationContext
{
    private readonly Dictionary<string, object>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    public ConfigurationContext()
    {
        Providers ??= new List<IConfigurationProvider>();
        Properties ??= new Dictionary<string, object>();
        _lookup = Properties.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public Dictionary<string, object> Properties { get; init; }
    IDictionary<string, object> IConfigurationContext.Properties => Properties;
    public IEnumerable<IConfigurationProvider> Providers { get; init; } 

    public T? GetProperty<T>(string key)
    {
        var span = key.AsSpan();

        if (_lookup.TryGetValue(span, out var value) && value is T type)
        {
            return type;
        }

        return default!;
    }
}
