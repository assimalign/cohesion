using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public static class ConfigurationSectionExtensions
{

    public static IConfigurationSection Merge(this IConfigurationSection section, IConfigurationSection other)
    {
        if (section.Key != other.Key)
        {
            throw new Exception();
        }


    }
}
