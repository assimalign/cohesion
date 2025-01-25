using System;

namespace Assimalign.Cohesion.Configuration;

public static class ConfigurationEntryExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static bool IsValue(this IConfigurationEntry entry, out IConfigurationValue? value)
    {
        value = null;

        if (entry is IConfigurationValue)
        {
            value = (IConfigurationValue)entry;

            return true;
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <param name="section"></param>
    /// <returns></returns>
    public static bool IsSection(this IConfigurationEntry entry, out IConfigurationSection? section)
    {
        section = null;

        if (entry is IConfigurationSection)
        {
            section = (IConfigurationSection)entry;

            return true;
        }
        return false;
    }
}
