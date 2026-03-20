using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Used to build key/value based configuration settings for use in an application.
/// </summary>
public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly ConfigurationOptions _options;
    private readonly List<Func<ConfigurationBuilderContext, Task>> _registrations;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationBuilder()
    {
        _registrations = [];
        _options = new ConfigurationOptions();
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
    public ConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddProvider(context => Task.FromResult(configure.Invoke(context)));
    }

    /// <summary>
    /// Registers an asynchronous provider factory.
    /// </summary>
    /// <param name="configure">The asynchronous provider factory.</param>
    /// <returns>The current builder.</returns>
    public ConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _registrations.Add(async context =>
        {
            IConfigurationProvider provider = await configure.Invoke(context).ConfigureAwait(false);

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

        cancellationTokenSource.CancelAfter(_options.LoadTimeout);

        var context = new ConfigurationBuilderContext(_options.LoadTimeout, _options.Providers);

        foreach (var registration in _registrations)
        {
            await registration.Invoke(context).ConfigureAwait(false);
        }

        foreach (var provider in context.Providers)
        {
            try
            {
                await provider.LoadAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
            }
        }

        return new Configuration(_options.CreateSnapshot(context.Providers));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static ConfigurationBuilder Create(Action<ConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ConfigurationOptions();
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

    IConfigurationBuilder IConfigurationBuilder.AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> provider)
    {
        return AddProvider(provider);
    }

    IConfiguration IConfigurationBuilder.Build()
    {
        return Build();
    }

    async ValueTask<IConfiguration> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken)
    {
        return await BuildAsync(cancellationToken).ConfigureAwait(false);
    }
}
