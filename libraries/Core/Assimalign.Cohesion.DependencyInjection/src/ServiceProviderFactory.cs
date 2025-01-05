using System;
using System.Linq;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// Avoid using this factory implementation within core application code. This is meant to be a way of managing service containers
/// from a parent level. For example 
/// </remarks>
public sealed class ServiceProviderFactory
{
    private static string defaultKey = Guid.NewGuid().ToString("N");

    private static Factory factory = new();
    private static ConcurrentDictionary<string, Func<IServiceProvider>> providers = new(StringComparer.CurrentCultureIgnoreCase);

    public ServiceProviderFactory Register(Action<IServiceProviderBuilder> configure)
    {
        IServiceProviderBuilder builder = new ServiceProviderBuilder();

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return factory;
        });

        builder.Add(descriptor);

        providers[defaultKey] = () => builder.Build();

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, IServiceCollection services)
    {
        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(factory);

        services.Add(descriptor);

        providers.TryAdd(serviceProviderName, () =>
        {
            return new ServiceProvider(services, ServiceProviderOptions.Default);
        });

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, Action<IServiceProviderBuilder> configure)
    {
        IServiceProviderBuilder builder = new ServiceProviderBuilder();

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return factory;
        });

        builder.Add(descriptor);

        providers[serviceProviderName] = () => builder.Build();

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, ServiceProviderOptions options, Action<IServiceProviderBuilder> configure)
    {
        IServiceProviderBuilder builder = new ServiceProviderBuilder(options);

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return factory;
        });

        builder.Add(descriptor);

        providers[serviceProviderName] = () => builder.Build();

        return this;
    }

    public IServiceProviderFactory Build() => factory;

    private partial class Factory : IServiceProviderFactory
    {
        IServiceProvider IServiceProviderFactory.Create()
        {
            if (!providers.Any())
            {
                throw new InvalidOperationException("No IServiceProvider's have been registered.");
            }
            if (providers.TryGetValue(defaultKey, out var provider))
            {
                return provider.Invoke();
            }

            throw new Exception("Provider does not exist");
        }

        IServiceProvider IServiceProviderFactory.Create(string serviceProviderName)
        {
            if (!providers.Any())
            {
                throw new InvalidOperationException("No IServiceProvider's have been registered.");
            }
            if (string.IsNullOrEmpty(serviceProviderName))
            {
                throw new ArgumentNullException(nameof(serviceProviderName));
            }
            if (providers.TryGetValue(serviceProviderName, out var provider))
            {
                return provider.Invoke();
            }

            throw new Exception("Provider does not exist");
        }

    
    }
}
