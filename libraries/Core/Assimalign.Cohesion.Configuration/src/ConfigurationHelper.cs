using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public static class ConfigurationHelperConfigurationHelper
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static IConfigurationEntry Compose(KeyPath path, string? value)
    {
        if (path.IsComposite)
        {
            return new ConfigurationSection(path[0])
            {
                Compose(path--, value)
            };
        }
        else
        {
            return new ConfigurationValue(path[0], value);
        }
    }
}
