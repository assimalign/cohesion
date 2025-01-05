using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// When does
/// </summary>
[DebuggerDisplay("Lifetime = {Lifetime}, ServiceType = {ServiceType}, ImplementationType = {ImplementationType}")]
public sealed class ServiceDescriptor
{

    /// <summary>
    /// Initializes a new implementationInstance of <see cref="ServiceDescriptor"/> with the specified <paramref name="implementationType"/>.
    /// </summary>
    /// <param name="serviceType">The <see cref="Type"/> of the service.</param>
    /// <param name="implementationType">The <see cref="Type"/> implementing the service.</param>
    /// <param name="lifetime">The <see cref="ServiceLifetime"/> of the service.</param>
    /// <exception cref="ArgumentNullException"> Neither <paramref name="serviceType"/> or <paramref name="implementationType"/> can be null.</exception>
    /// <exception cref="ArgumentException"><paramref name="implementationType"/> must be assignable to <paramref name="serviceType"/></exception>
    public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
        : this(serviceType, lifetime)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }
        //if (!serviceType.IsAssignableFrom(implementationType))
        //{
        //    throw new ArgumentException($"The type '{implementationType}' is not assignable to '{serviceType}'.");
        //}

        ImplementationType = implementationType;
    }

    /// <summary>
    /// Initializes a new implementationInstance of <see cref="ServiceDescriptor"/> with the specified <paramref name="implementationInstance"/>
    /// as a <see cref="ServiceLifetime.Singleton"/>.
    /// </summary>
    /// <param name="serviceType">The <see cref="Type"/> of the service.</param>
    /// <param name="implementationInstance">The implementationInstance implementing the service.</param>
    /// <exception cref="ArgumentNullException"> Neither <paramref name="serviceType"/> or <paramref name="implementationInstance"/> can be null.</exception>
    /// <exception cref="ArgumentException">Type of <paramref name="implementationInstance"/> must be assignable to <paramref name="serviceType"/></exception>
    public ServiceDescriptor(Type serviceType, object implementationInstance) 
        : this(serviceType, ServiceLifetime.Singleton)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationInstance == null)
        {
            throw new ArgumentNullException(nameof(implementationInstance));
        }
        //if (!serviceType.IsAssignableFrom(implementationInstance.GetType()))
        //{
        //    throw new ArgumentException($"The type '{implementationInstance.GetType()}' is not assignable to '{serviceType}'.");
        //}

        ImplementationInstance = implementationInstance;
    }

    /// <summary>
    /// Initializes a new implementationInstance of <see cref="ServiceDescriptor"/> with the specified <paramref name="factory"/>.
    /// </summary>
    /// <param name="serviceType">The <see cref="Type"/> of the service.</param>
    /// <param name="factory">A factory used for creating service instances.</param>
    /// <param name="lifetime">The <see cref="ServiceLifetime"/> of the service.</param>
    /// <exception cref="ArgumentNullException"> Neither <paramref name="serviceType"/> or <paramref name="factory"/> can be null.</exception>
    /// <exception cref="ArgumentException">The return type of <paramref name="factory"/> must be assignable to <paramref name="serviceType"/></exception>
    public ServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime)
        : this(serviceType, lifetime)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        var returnType = factory.GetType().GenericTypeArguments[1];

        //if (!serviceType.IsAssignableFrom(returnType))
        //{
        //    throw new ArgumentException($"The type '{returnType}' is not assignable to '{serviceType}'.");
        //}

        ImplementationFactory = factory;
    }

    private ServiceDescriptor(Type serviceType, ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
        ServiceType = serviceType;
    }

    /// <summary>
    /// 
    /// </summary>
    public ServiceLifetime Lifetime { get; }
    /// <summary>
    /// 
    /// </summary>
    public Type ServiceType { get; }
    /// <summary>
    /// 
    /// </summary>
    public Type? ImplementationType { get; }
    /// <summary>
    /// 
    /// </summary>
    public object? ImplementationInstance { get; }
    /// <summary>
    /// 
    /// </summary>
    public Func<IServiceProvider, object>? ImplementationFactory { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var lifetime = $"{nameof(ServiceType)}: {ServiceType} {nameof(Lifetime)}: {Lifetime} ";

        if (ImplementationType != null)
        {
            return lifetime + $"{nameof(ImplementationType)}: {ImplementationType}";
        }

        if (ImplementationFactory != null)
        {
            return lifetime + $"{nameof(ImplementationFactory)}: {ImplementationFactory.Method}";
        }

        return lifetime + $"{nameof(ImplementationInstance)}: {ImplementationInstance}";
    }

    internal Type GetImplementationType()
    {
        if (ImplementationType != null)
        {
            return ImplementationType;
        }
        if (ImplementationInstance != null)
        {
            return ImplementationInstance.GetType();
        }
        if (ImplementationFactory != null)
        {
            var typeArguments = ImplementationFactory.GetType().GenericTypeArguments;

            Debug.Assert(typeArguments.Length == 2);

            return typeArguments[1];
        }

        Debug.Assert(false, "ImplementationType, ImplementationInstance or ImplementationFactory must be non null");
        return null;
    }


    #region Static Methods

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// and the <see cref="ServiceLifetime.Transient"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Transient<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        return Describe<TService, TImplementation>(ServiceLifetime.Transient);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="service"/> and <paramref name="implementationType"/>
    /// and the <see cref="ServiceLifetime.Transient"/> lifetime.
    /// </summary>
    /// <param name="service">The type of the service.</param>
    /// <param name="implementationType">The type of the implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Transient(Type service,Type implementationType)
    {
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }

        return Describe(service, implementationType, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Transient"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Transient<TService, TImplementation>(
        Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Transient"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Transient<TService>(Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="service"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Transient"/> lifetime.
    /// </summary>
    /// <param name="service">The type of the service.</param>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Transient(Type service, Func<IServiceProvider, object> implementationFactory)
    {
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(service, implementationFactory, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// and the <see cref="ServiceLifetime.Scoped"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Scoped<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        return Describe<TService, TImplementation>(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="service"/> and <paramref name="implementationType"/>
    /// and the <see cref="ServiceLifetime.Scoped"/> lifetime.
    /// </summary>
    /// <param name="service">The type of the service.</param>
    /// <param name="implementationType">The type of the implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Scoped(Type service, Type implementationType)
    {
        return Describe(service, implementationType, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Scoped"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Scoped<TService, TImplementation>(Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Scoped"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Scoped<TService>(Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="service"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Scoped"/> lifetime.
    /// </summary>
    /// <param name="service">The type of the service.</param>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Scoped(Type service, Func<IServiceProvider, object> implementationFactory)
    {
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(service, implementationFactory, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        return Describe<TService, TImplementation>(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="service"/> and <paramref name="implementationType"/>
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <param name="service">The type of the service.</param>
    /// <param name="implementationType">The type of the implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton(Type service, Type implementationType)
    {
        if (service == null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }

        return Describe(service, implementationType, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <typeparamref name="TImplementation"/>,
    /// <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton<TService, TImplementation>(Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton<TService>(Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(typeof(TService), implementationFactory, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="serviceType"/>, <paramref name="implementationFactory"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <param name="serviceType">The type of the service.</param>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton(Type serviceType, Func<IServiceProvider, object> implementationFactory)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Describe(serviceType, implementationFactory, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <typeparamref name="TService"/>, <paramref name="implementationInstance"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <param name="implementationInstance">The implementationInstance of the implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton<TService>(TService implementationInstance)
        where TService : class
    {
        if (implementationInstance == null)
        {
            throw new ArgumentNullException(nameof(implementationInstance));
        }

        return Singleton(typeof(TService), implementationInstance);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="serviceType"/>, <paramref name="implementationInstance"/>,
    /// and the <see cref="ServiceLifetime.Singleton"/> lifetime.
    /// </summary>
    /// <param name="serviceType">The type of the service.</param>
    /// <param name="implementationInstance">The implementationInstance of the implementation.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Singleton(Type serviceType, object implementationInstance)
    {
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (implementationInstance == null)
        {
            throw new ArgumentNullException(nameof(implementationInstance));
        }

        return new ServiceDescriptor(serviceType, implementationInstance);
    }    

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="serviceType"/>, <paramref name="implementationType"/>,
    /// and <paramref name="lifetime"/>.
    /// </summary>
    /// <param name="serviceType">The type of the service.</param>
    /// <param name="implementationType">The type of the implementation.</param>
    /// <param name="lifetime">The lifetime of the service.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Describe(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        return new ServiceDescriptor(serviceType, implementationType, lifetime);
    }

    /// <summary>
    /// Creates an implementationInstance of <see cref="ServiceDescriptor"/> with the specified
    /// <paramref name="serviceType"/>, <paramref name="implementationFactory"/>,
    /// and <paramref name="lifetime"/>.
    /// </summary>
    /// <param name="serviceType">The type of the service.</param>
    /// <param name="implementationFactory">A factory to create new instances of the service implementation.</param>
    /// <param name="lifetime">The lifetime of the service.</param>
    /// <returns>A new implementationInstance of <see cref="ServiceDescriptor"/>.</returns>
    public static ServiceDescriptor Describe(Type serviceType, Func<IServiceProvider, object> implementationFactory, ServiceLifetime lifetime)
    {
        return new ServiceDescriptor(serviceType, implementationFactory, lifetime);
    }

    private static ServiceDescriptor Describe<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        return Describe(
            typeof(TService),
            typeof(TImplementation),
            lifetime: lifetime);
    }
    
    #endregion
}
