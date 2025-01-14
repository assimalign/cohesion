using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public sealed class ConfigurationOptions
{
    public ConfigurationProviderStrategy ProviderStrategy { get; set; }
}


public enum ConfigurationProviderStrategy
{
    /// <summary>
    /// Only Allows unique keys across all providers.
    /// </summary>
    UniqueOnly,


}