using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public static class ConfigurationProviderExtensions
{

    public static IConfigurationEntry? Get(this IConfigurationProvider provider, Key key, KeyComparison comparison)
    {
        foreach (var entry in provider.GetEntries())
        {
            if (entry.Key.Equals(key, comparison))
            {
                return entry;
            }
        }

        return null;
    }

    public static void Set(this IConfigurationProvider provider, IConfigurationEntry entry, KeyComparison comparison)
    {

    }
}
