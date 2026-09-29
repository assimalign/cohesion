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
    private static string _defaultKey = Guid.NewGuid().ToString("N");

    private static Factory _factory = new();
    private static ConcurrentDictionary<string, Func<IServiceProvider>> _providers = new(StringComparer.CurrentCultureIgnoreCase);

    public ServiceProviderFactory Register(Action<ServiceProviderBuilder> configure)
    {
        ServiceProviderBuilder builder = new ServiceProviderBuilder();

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return _factory;
        });

        builder.Add(descriptor);

        _providers[_defaultKey] = () => ((IServiceProviderBuilder)builder).Build();

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, ServiceContainer services)
    {
        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(_factory);

        services.Register(descriptor);

        _providers.TryAdd(serviceProviderName, () =>
        {
            return new ServiceProvider(services, ServiceProviderOptions.Default);
        });

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, Action<ServiceProviderBuilder> configure)
    {
        ServiceProviderBuilder builder = new ServiceProviderBuilder();

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return _factory;
        });

        builder.Add(descriptor);

        _providers[serviceProviderName] = () => ((IServiceProviderBuilder)builder).Build();

        return this;
    }
    public ServiceProviderFactory Register(string serviceProviderName, ServiceProviderOptions options, Action<IServiceProviderBuilder> configure)
    {
        IServiceProviderBuilder builder = new ServiceProviderBuilder(options);

        configure.Invoke(builder);

        var descriptor = ServiceDescriptor.Singleton<IServiceProviderFactory>(serviceProvider =>
        {
            return _factory;
        });

        builder.Add(descriptor);

        _providers[serviceProviderName] = () => builder.Build();

        return this;
    }

    public IServiceProviderFactory Build() => _factory;

    private partial class Factory : IServiceProviderFactory
    {
        IServiceProvider IServiceProviderFactory.Create()
        {
            if (!_providers.Any())
            {
                throw new InvalidOperationException("No IServiceProvider's have been registered.");
            }
            if (_providers.TryGetValue(_defaultKey, out var provider))
            {
                return provider.Invoke();
            }

            throw new Exception("Provider does not exist");
        }

        IServiceProvider IServiceProviderFactory.Create(string serviceProviderName)
        {
            if (!_providers.Any())
            {
                throw new InvalidOperationException("No IServiceProvider's have been registered.");
            }
            if (string.IsNullOrEmpty(serviceProviderName))
            {
                throw new ArgumentNullException(nameof(serviceProviderName));
            }
            if (_providers.TryGetValue(serviceProviderName, out var provider))
            {
                return provider.Invoke();
            }

            throw new Exception("Provider does not exist");
        }

    
    }
}
