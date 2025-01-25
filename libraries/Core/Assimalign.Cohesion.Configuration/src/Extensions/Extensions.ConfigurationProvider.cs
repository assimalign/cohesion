using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public static class ConfigurationProviderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static bool TryGetValue(this IConfigurationProvider provider, Key key, out object? value)
    {
        value = null;

        var entries = provider.GetEntries();
        
        if (entries is IList<IConfigurationEntry> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];

                if (entry is IConfigurationValue v && entry.Key == key)
                {
                    value = v.Value;
                    return true;
                }
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                if (entry is IConfigurationValue v && entry.Key == key)
                {
                    value = v.Value;
                    return true;
                }
            }
        }

        return false;
    }

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
