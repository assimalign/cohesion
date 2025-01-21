using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Internal;

internal class ConfigurationBuilderContext : IConfigurationBuilderContext
{
    public ConfigurationBuilderContext()
    {
        Properties = new Dictionary<string, object>();
        Providers ??= new List<IConfigurationProvider>();
    }

    public IDictionary<string, object> Properties { get; }
    public IEnumerable<IConfigurationProvider> Providers { get; init; } 
}
