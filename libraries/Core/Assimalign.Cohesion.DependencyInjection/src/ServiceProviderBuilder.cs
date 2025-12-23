using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.DependencyInjection;

public sealed class ServiceProviderBuilder : IServiceProviderBuilder, IDisposable
{
    private static readonly ConcurrentDictionary<int, IServiceCollection> services = new();
    private static readonly ConcurrentDictionary<int, IServiceProvider> providers = new();


    private readonly ServiceProviderOptions _options = ServiceProviderOptions.Default;
    public ServiceProviderBuilder()
    {
    }

    public ServiceProviderBuilder(ServiceProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    public IServiceCollection Services => services.GetOrAdd(this.GetHashCode(), new ServiceCollection());    
    public IServiceProviderBuilder Add(ServiceDescriptor serviceDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serviceDescriptor);

        Services.Add(serviceDescriptor);

        return this;
    }
    IServiceProvider IServiceProviderBuilder.Build()
    {
        return new ServiceProvider(
            Services, 
            _options);
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
