using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Configuration.Internal;

/// <summary>
/// Used to build key/value based configuration settings for use in an application.
/// </summary>
public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly IList<Action<IConfigurationContext>> onAdd;

    public ConfigurationBuilder()
    {
        this.onAdd = new List<Action<IConfigurationContext>>();
    }

    public IConfigurationBuilder AddProvider(Func<IConfigurationContext, IConfigurationProvider> func)
    {
        if (func is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(func));
        }
        onAdd.Add(context =>
        {
            var provider = func.Invoke(context);

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
        var context = new ConfigurationContext();

        foreach (var action in onAdd)
        {
            action.Invoke(context);
        }

        return new ConfigurationRoot(context.Providers);
    }
}
