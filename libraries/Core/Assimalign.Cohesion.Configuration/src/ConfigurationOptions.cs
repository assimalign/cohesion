using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

public sealed class ConfigurationOptions
{
    private ConfigurationSetStrategy setStrategy = ConfigurationSetStrategy.ExistingOnly;

    public ConfigurationOptions()
    {
        
    }

    /// <summary>
    /// 
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
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

    /// <summary>
    /// A list of providers to use for the configuration root.
    /// </summary>
    public List<IConfigurationProvider> Providers { get; } = new List<IConfigurationProvider>();

    /// <summary>
    /// The default options
    /// </summary>
    public static ConfigurationOptions Default { get; } = new();
}
