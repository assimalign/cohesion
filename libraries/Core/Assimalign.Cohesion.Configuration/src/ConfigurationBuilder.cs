using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Used to build key/value based configuration settings for use in an application.
/// </summary>
public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly ConfigurationOptions _options;
    private readonly List<Func<ConfigurationBuilderContext, Task>> _builds;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationBuilder()
    {
        _builds = new List<Func<ConfigurationBuilderContext, Task>>();
        _options ??= ConfigurationOptions.Default;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ConfigurationBuilder(ConfigurationOptions options) : this()
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<ConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddProvider(
            new Func<ConfigurationBuilderContext, Task<IConfigurationProvider>>(context =>
            {
                return Task.FromResult(configure.Invoke(context));
            }));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<ConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _builds.Add(async context =>
        {
            IConfigurationProvider provider = await configure.Invoke(context);

            InvalidOperationException.ThrowIf(
                context.HasProvider(provider.Name),
                $"The configuration provider: '{provider.Name}' has already been added.");

            context.AddProvider(provider);
        });
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public Configuration Build()
    {
        return BuildAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask<Configuration> BuildAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Set the timeout period for loading the configurations
        cancellationTokenSource.CancelAfter(_options.LoadTimeout);

        // Pass the list<Providers> ref to the builder context
        var context = new ConfigurationBuilderContext(_options.Providers);

        foreach (var func in _builds)
        {
            await func.Invoke(context);
        }
        foreach (var provider in context.Providers)
        {
            try
            {
                await provider.LoadAsync(cancellationTokenSource.Token);
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
            }
        }


        return new Configuration(_options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static ConfigurationBuilder Create(Action<ConfigurationOptions> configure)
    {
        var options = new ConfigurationOptions();

        ArgumentNullException.ThrowIfNull(configure, nameof(configure));
        
        configure.Invoke(options);

        return new ConfigurationBuilder(options);
    }

    IConfigurationBuilder IConfigurationBuilder.AddProvider(IConfigurationProvider provider)
    {
        return AddProvider(_ => provider);
    }
    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider)
    {
        return AddProvider(provider);
    }
    IConfiguration IConfigurationBuilder.Build()
    {
        return Build();
    }
    async ValueTask<IConfiguration> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken)
    {
        return await BuildAsync(cancellationToken);
    }
}
