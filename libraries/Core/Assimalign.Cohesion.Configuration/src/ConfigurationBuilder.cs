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
    private readonly ConfigurationOptions options;
    private readonly IList<Func<IConfigurationBuilderContext, Task>> funcs;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationBuilder()
    {
        this.funcs = new List<Func<IConfigurationBuilderContext, Task>>();
        this.options ??= ConfigurationOptions.Default;
    }

    public ConfigurationBuilder(ConfigurationOptions options) : this()
    {
        this.options = options;
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
        ThrowHelper.ThrowIfNull(configure, nameof(configure));

        this.funcs.Add(async context =>
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
        cancellationTokenSource.CancelAfter(options.LoadTimeout);

        // Pass the list<Providers> ref to the builder context
        var context = new ConfigurationBuilderContext()
        {
            Providers = options.Providers
        };

        foreach (var func in funcs)
        {
            await func.Invoke(context);
        }

        

        return new ConfigurationRoot(options);
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

    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        return AddProvider(configure);
    }

    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        return AddProvider(configure);
    }

    IConfigurationRoot IConfigurationBuilder.Build()
    {
        return Build();
    }

    async ValueTask<IConfigurationRoot> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken = default)
    {
        return await BuildAsync(cancellationToken);
    }

    #endregion

}
