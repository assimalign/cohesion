using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;



public sealed class ConfigurationOptions
{
    private ConfigurationSetStrategy _setStrategy;

    public ConfigurationOptions()
    {
        _setStrategy = ConfigurationSetStrategy.ExistingOnly;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public ConfigurationSetStrategy SetStrategy
    {
        get => _setStrategy;
        set => _setStrategy = ArgumentException.ThrowIfEnumNotDefined(value);
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
