using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Assimalign.Cohesion.Configuration.Internal;

internal class ConfigurationContext : IConfigurationContext
{
    private readonly List<IConfigurationProvider> providers;
    
    
    public ConfigurationContext()
    {
        providers = new List<IConfigurationProvider>();
        Properties = new ConcurrentDictionary<string, object>();
    }
    public IDictionary<string, object> Properties { get; }
    public IEnumerable<IConfigurationProvider> Providers { get; }
    public void Add(IConfigurationProvider provider)
    {
        providers.Add(provider);
    }
}
