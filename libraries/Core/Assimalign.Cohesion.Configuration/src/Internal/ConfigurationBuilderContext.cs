using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Internal;

internal class ConfigurationBuilderContext : IConfigurationBuilderContext
{
    public ConfigurationBuilderContext()
    {
        Properties = new Dictionary<string, object>();
        Providers = new ConcurrentBag<IConfigurationProvider>();
    }

    public IDictionary<string, object> Properties { get; } = default!;
    public IEnumerable<IConfigurationProvider> Providers { get; init; } = default!;
}
