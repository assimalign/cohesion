using System;
using System.Linq;
using System.Collections.Generic;
using Assimalign.Cohesion.DependencyInjection.Properties;

namespace Assimalign.Cohesion.DependencyInjection;

public static partial class ServiceProviderBuilderExtensions
{
    public static bool TryAddTransient(this IServiceProviderBuilder builder, Type service)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        var descriptor = ServiceDescriptor.Transient(service, service);
        
        return TryAdd(builder, descriptor);
    }
    public static bool TryAddTransient(this IServiceProviderBuilder builder, Type service, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException("implementationType");
        }
        
        var descriptor = ServiceDescriptor.Transient(service, implementationType);
        
        return TryAdd(builder, descriptor);
    }
    public static bool TryAddTransient(this IServiceProviderBuilder builder, Type service, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }
        
        var descriptor = ServiceDescriptor.Transient(service, implementationFactory);

        return TryAdd(builder, descriptor);
    }
    public static bool TryAddTransient<TService>(this IServiceProviderBuilder builder) where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddTransient(builder, typeof(TService), typeof(TService));
    }
    public static bool TryAddTransient<TService, TImplementation>(this IServiceProviderBuilder builder) where TService : class where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddTransient(builder, typeof(TService), typeof(TImplementation));
    }
    public static bool TryAddTransient<TService>(this IServiceProviderBuilder builder, Func<IServiceProvider, TService> implementationFactory) where TService : class
    {
        return TryAdd(builder, ServiceDescriptor.Transient(implementationFactory));
    }
    public static bool TryAddScoped(this IServiceProviderBuilder builder, Type service)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        
        var descriptor = ServiceDescriptor.Scoped(service, service);
        
        return TryAdd(builder, descriptor);
    }
    public static bool TryAddScoped(this IServiceProviderBuilder builder, Type service, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }
        
        var descriptor = ServiceDescriptor.Scoped(service, implementationType);
        
        return TryAdd(builder, descriptor);
    }
    public static bool TryAddScoped(this IServiceProviderBuilder builder, Type service, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }
        
        var descriptor = ServiceDescriptor.Scoped(service, implementationFactory);

        return TryAdd(builder, descriptor);
    }
    public static bool TryAddScoped<TService>(this IServiceProviderBuilder builder) where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddScoped(builder, typeof(TService), typeof(TService));
    }
    public static bool TryAddScoped<TService, TImplementation>(this IServiceProviderBuilder builder) where TService : class where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddScoped(builder, typeof(TService), typeof(TImplementation));
    }
    public static bool TryAddScoped<TService>(this IServiceProviderBuilder builder, Func<IServiceProvider, TService> implementationFactory) where TService : class
    {
        return TryAdd(builder, ServiceDescriptor.Scoped(implementationFactory));
    }
    public static bool TryAddSingleton(this IServiceProviderBuilder builder, Type service)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        var descriptor = ServiceDescriptor.Singleton(service, service);

        return TryAdd(builder, descriptor);
    }
    public static bool TryAddSingleton(this IServiceProviderBuilder builder, Type service, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }
        
        var descriptor = ServiceDescriptor.Singleton(service, implementationType);

        return TryAdd(builder, descriptor);
    }
    public static bool TryAddSingleton(this IServiceProviderBuilder builder, Type service, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }
        var descriptor = ServiceDescriptor.Singleton(service, implementationFactory);

        return TryAdd(builder, descriptor);
    }
    public static bool TryAddSingleton<TService>(this IServiceProviderBuilder builder) where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddSingleton(builder, typeof(TService), typeof(TService));
    }
    public static bool TryAddSingleton<TService, TImplementation>(this IServiceProviderBuilder builder) where TService : class where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        return TryAddSingleton(builder, typeof(TService), typeof(TImplementation));
    }
    public static bool TryAddSingleton<TService>(this IServiceProviderBuilder builder, TService instance) where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (instance == null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        var descriptor = ServiceDescriptor.Singleton(typeof(TService), instance);
        
        return TryAdd(builder, descriptor);
    }
    public static bool TryAddSingleton<TService>(this IServiceProviderBuilder services, Func<IServiceProvider, TService> implementationFactory) where TService : class
    {
        return TryAdd(services, ServiceDescriptor.Singleton(implementationFactory));
    }
    public static bool TryAddEnumerable(this IServiceProviderBuilder builder, ServiceDescriptor descriptor)
    {
        var descriptor2 = descriptor;

        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (descriptor2 == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        var implementationType = descriptor2.GetImplementationType();
        
        if (implementationType == typeof(object) || implementationType == descriptor2.ServiceType)
        {
            throw new ArgumentException(Resources.GetTryAddIndistinguishableTypeToEnumerableExceptionMessage(implementationType, descriptor2.ServiceType), "descriptor");
        }
        if (!builder.Services.Any((ServiceDescriptor d) => d.ServiceType == descriptor2.ServiceType && d.GetImplementationType() == implementationType))
        {
            builder.Services.Add(descriptor2);
            return true;
        }
        return false;
    }
    public static void TryAddEnumerable(this IServiceProviderBuilder services, IEnumerable<ServiceDescriptor> descriptors)
    {
        if (services == null)
        {
            throw new ArgumentNullException("builder");
        }
        if (descriptors == null)
        {
            throw new ArgumentNullException("descriptors");
        }
        foreach (ServiceDescriptor descriptor in descriptors)
        {
            TryAddEnumerable(services, descriptor);
        }
    }
    public static bool TryAdd(this IServiceProviderBuilder builder, ServiceDescriptor descriptor)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (descriptor is null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }
        if (!builder.Services.Any((ServiceDescriptor serviceDescriptor) => serviceDescriptor.ServiceType == descriptor.ServiceType))
        {
            builder.Add(descriptor);
            return true;
        }
        return false;
    }


    public static IServiceProviderBuilder Replace(this IServiceProviderBuilder builder, ServiceDescriptor descriptor)
    {
        var descriptor2 = descriptor;

        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (descriptor2 == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        var serviceDescriptor = builder.Services.FirstOrDefault((ServiceDescriptor s) => s.ServiceType == descriptor2.ServiceType);
        
        if (serviceDescriptor != null)
        {
            builder.Services.Remove(serviceDescriptor);
        }

        builder.Add(descriptor2);
        
        return builder;
    }
    public static IServiceProviderBuilder RemoveAll<T>(this IServiceProviderBuilder builder)
    {
        return RemoveAll(builder, typeof(T));
    }
    public static IServiceProviderBuilder RemoveAll(this IServiceProviderBuilder builder, Type serviceType)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        for (int num = builder.Services.Count - 1; num >= 0; num--)
        {
            var serviceDescriptor = builder.Services[num];
            
            if (serviceDescriptor.ServiceType == serviceType)
            {
                builder.Services.RemoveAt(num);
            }
        }
        return builder;
    }
}
