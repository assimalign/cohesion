using System;

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

    /// <summary>
    /// The amount of time to wait before canceling the load or reload.
    /// </summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.Zero;


    public static ConfigurationRootOptions Default { get; } = new();
}
