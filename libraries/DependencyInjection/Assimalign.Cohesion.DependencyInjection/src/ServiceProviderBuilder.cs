using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.DependencyInjection;

public sealed class ServiceProviderBuilder : IServiceProviderBuilder, IDisposable
{
    private static readonly ConcurrentDictionary<int, IServiceContainer> services = new();
    private static readonly ConcurrentDictionary<int, IServiceProvider> providers = new();


    private readonly ServiceProviderOptions _options = ServiceProviderOptions.Default;
    public ServiceProviderBuilder()
    {
    }

    public ServiceProviderBuilder(ServiceProviderOptions options)
    {
        _options = ArgumentNullException.ThrowIfNull<ServiceProviderOptions>(options);
    }

    public IServiceContainer Services => services.GetOrAdd(this.GetHashCode(), new ServiceContainer());
    
    public IServiceProviderBuilder Add(ServiceDescriptor serviceDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serviceDescriptor);

        Services.Register(serviceDescriptor);

        return this;
    }
    IServiceProvider IServiceProviderBuilder.Build()
    {
        return new ServiceProvider((ServiceContainer)Services, _options);
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
