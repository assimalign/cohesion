using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Configuration.Internal;

/// <summary>
/// Used to build key/value based configuration settings for use in an application.
/// </summary>
public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly ConfigurationOptions _options;
    private readonly List<Func<IConfigurationContext, Task>> _builds;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationBuilder()
    {
        _builds = new List<Func<IConfigurationContext, Task>>();
        _options ??= ConfigurationOptions.Default;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ConfigurationBuilder(ConfigurationOptions options) : this()
    {
        _options = ThrowHelper.ThrowIfNull(options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<IConfigurationContext, IConfigurationProvider> configure)
    {
        return AddProvider(
            new Func<IConfigurationContext, Task<IConfigurationProvider>>(context =>
            {
                return Task.FromResult(configure.Invoke(context));
            }));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<IConfigurationContext, Task<IConfigurationProvider>> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        _builds.Add(async context =>
        {
            var provider = await configure.Invoke(context);

            if (context.Providers.Any(p => p.Name == provider.Name))
            {
                ThrowHelper.ThrowInvalidOperationException($"The configuration provider: '{provider.Name}' has already been added.");
            }

            ((List<IConfigurationProvider>)context.Providers).Add(provider);
        });
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public ConfigurationRoot Build()
    {
        return BuildAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ConfigurationRoot> BuildAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Set the timeout period for loading the configurations
        cancellationTokenSource.CancelAfter(_options.LoadTimeout);

        // Pass the list<Providers> ref to the builder context
        var context = new ConfigurationContext()
        {
            Providers = _options.Providers
        };

        foreach (var func in _builds)
        {
            await func.Invoke(context);
        }

        if (_options.LoadProvidersOnBuild)
        {
            foreach (var provider in context.Providers)
            {
                await provider.LoadAsync(cancellationTokenSource.Token);
            }
        }

        return new ConfigurationRoot(_options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static ConfigurationBuilder Create(Action<ConfigurationOptions> configure)
    {
        ThrowHelper.ThrowIfNull(configure, nameof(configure));

        var options = new ConfigurationOptions();

        configure.Invoke(options);

        return new ConfigurationBuilder(options);
    }


    #region Interfaces

    IConfigurationBuilder IConfigurationBuilder.AddProvider(IConfigurationProvider provider)
    {
        return AddProvider(context => provider);
    }
    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationContext, IConfigurationProvider> configure)
    {
        return AddProvider(configure);
    }

    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationContext, Task<IConfigurationProvider>> configure)
    {
        return AddProvider(configure);
    }

    IConfigurationRoot IConfigurationBuilder.Build()
    {
        return Build();
    }

    async Task<IConfigurationRoot> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken = default)
    {
        return await BuildAsync(cancellationToken);
    }
    #endregion
}
