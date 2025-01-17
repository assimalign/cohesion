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
    private readonly IList<Func<IConfigurationContext, Task>> onAdd;

    public ConfigurationBuilder()
    {
        this.onAdd = new List<Func<IConfigurationContext, Task>>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public IConfigurationBuilder AddProvider(Func<IConfigurationContext, IConfigurationProvider> configure)
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
    public IConfigurationBuilder AddProvider(Func<IConfigurationContext, Task<IConfigurationProvider>> configure)
    {
        if (configure is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(configure));
        }
        onAdd.Add(async context =>
        {
            var provider = await configure.Invoke(context);

            if (context.Providers.Any(p => p.Name == provider.Name))
            {
                ThrowHelper.ThrowInvalidOperationException($"The configuration provider: '{provider.Name}' has already been added.");
            }

            ((ConfigurationContext)context).Add(provider);
        });
        return this;
    }

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> with keys and values from the set of providers registered in
    /// <see cref="Sources"/>.
    /// </summary>
    /// <returns>An <see cref="IConfigurationRoot"/> with keys and values from the registered providers.</returns>
    public IConfigurationRoot Build()
    {
        return BuildAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask<IConfigurationRoot> BuildAsync(CancellationToken cancellationToken = default)
    {
        var context = new ConfigurationContext();
        var tasks = onAdd.Select(func => func.Invoke(context));

        await Task.WhenAll(tasks);

        return new ConfigurationRoot(context.Providers);
    }
}
