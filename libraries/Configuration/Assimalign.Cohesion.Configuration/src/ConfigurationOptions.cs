using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

public sealed class ConfigurationOptions
{
    private ConfigurationSetStrategy _setStrategy;
    private TimeSpan _loadTimeout;

    public ConfigurationOptions()
    {
        _setStrategy = ConfigurationSetStrategy.ExistingOnly;
        _loadTimeout = TimeSpan.Zero;
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
    public TimeSpan LoadTimeout
    {
        get => _loadTimeout;
        set
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _loadTimeout = value;
        }
    }

    /// <summary>
    /// A list of providers to use for the configuration root.
    /// </summary>
    public List<IConfigurationProvider> Providers { get; } = [];

    /// <summary>
    /// The default options.
    /// </summary>
    public static ConfigurationOptions Default => new();

    internal ConfigurationOptions CreateSnapshot(IEnumerable<IConfigurationProvider> providers)
    {
        var options = new ConfigurationOptions
        {
            SetStrategy = SetStrategy,
            LoadTimeout = LoadTimeout
        };

        options.Providers.AddRange(providers);

        return options;
    }
}
