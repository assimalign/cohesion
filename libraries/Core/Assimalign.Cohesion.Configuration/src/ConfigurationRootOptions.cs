using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

public sealed class ConfigurationRootOptions
{

    private ConfigurationSetStrategy setStrategy = ConfigurationSetStrategy.ExistingOnly;

    public ConfigurationRootOptions()
    {
        
    }

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationSetStrategy SetStrategy
    {
        get => setStrategy;
        set
        {
            if (!Enum.IsDefined(typeof(ConfigurationSetStrategy), value))
            {
                ThrowHelper.ThrowArgumentException("");
            }
            setStrategy = value;
        }
    }


    public static ConfigurationRootOptions Default { get; } = new();
}
