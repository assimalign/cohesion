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
        _options = ThrowHelper.ThrowIfNull(options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        return AddProvider(
            new Func<IConfigurationBuilderContext, Task<IConfigurationProvider>>(context =>
            {
                return Task.FromResult(configure.Invoke(context));
            }));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public ConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        _builds.Add(async context =>
        {
            var provider = await configure.Invoke(context);

            if (context.HasProvider(provider.Name))
            {
                ThrowHelper.ThrowInvalidOperationException($"The configuration provider: '{provider.Name}' has already been added.");
            }

            context.AddProvider(provider);
        });
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public ConfigurationRoot Build()
    {
        return BuildAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask<ConfigurationRoot> BuildAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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


        return new ConfigurationRoot(_options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static ConfigurationBuilder Create(Action<ConfigurationOptions> configure)
    {
        var options = new ConfigurationOptions();

        ThrowHelper.ThrowIfNull(configure, nameof(configure)).Invoke(options);

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
    IConfigurationRoot IConfigurationBuilder.Build()
    {
        return Build();
    }
    async ValueTask<IConfigurationRoot> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken)
    {
        return await BuildAsync(cancellationToken);
    }
}
