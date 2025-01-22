using System;

namespace Assimalign.Cohesion.DependencyInjection;

public static partial class ServiceProviderBuilderExtensions
{
    /// <summary>
    /// Adds a transient service of the type specified in <paramref name="serviceType"/> with an
    /// implementation of the type specified in <paramref name="implementationType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceProviderBuilder"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationType">The implementation type of the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient(this IServiceProviderBuilder builder, Type serviceType, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }

        return Add(builder, serviceType, implementationType, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Adds a transient service of the type specified in <paramref name="serviceType"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceProviderBuilder"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient(this IServiceProviderBuilder builder, Type serviceType, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Add(builder, serviceType, implementationFactory, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Adds a transient service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient<TService, TImplementation>(this IServiceProviderBuilder builder)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddTransient(typeof(TService), typeof(TImplementation));
    }

    /// <summary>
    /// Adds a transient service of the type specified in <paramref name="serviceType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register and the implementation to use.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient(this IServiceProviderBuilder builder, Type serviceType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        return builder.AddTransient(serviceType, serviceType);
    }

    /// <summary>
    /// Adds a transient service of the type specified in <typeparamref name="TService"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient<TService>(this IServiceProviderBuilder builder)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddTransient(typeof(TService));
    }

    /// <summary>
    /// Adds a transient service of the type specified in <typeparamref name="TService"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient<TService>(this IServiceProviderBuilder builder, Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddTransient(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a transient service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation" /> using the
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Transient"/>
    public static IServiceProviderBuilder AddTransient<TService, TImplementation>(this IServiceProviderBuilder builder, Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddTransient(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <paramref name="serviceType"/> with an
    /// implementation of the type specified in <paramref name="implementationType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationType">The implementation type of the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped(this IServiceProviderBuilder builder, Type serviceType, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }

        return Add(builder, serviceType, implementationType, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <paramref name="serviceType"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped(this IServiceProviderBuilder builder, Type serviceType, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Add(builder, serviceType, implementationFactory, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped<TService, TImplementation>(this IServiceProviderBuilder builder)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddScoped(typeof(TService), typeof(TImplementation));
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <paramref name="serviceType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register and the implementation to use.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped(this IServiceProviderBuilder builder, Type serviceType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        return builder.AddScoped(serviceType, serviceType);
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <typeparamref name="TService"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped<TService>(this IServiceProviderBuilder builder)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddScoped(typeof(TService));
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <typeparamref name="TService"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped<TService>(this IServiceProviderBuilder builder, Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddScoped(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a scoped service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation" /> using the
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Scoped"/>
    public static IServiceProviderBuilder AddScoped<TService, TImplementation>(this IServiceProviderBuilder builder, Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddScoped(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <paramref name="serviceType"/> with an
    /// implementation of the type specified in <paramref name="implementationType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationType">The implementation type of the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton(this IServiceProviderBuilder builder, Type serviceType, Type implementationType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (implementationType == null)
        {
            throw new ArgumentNullException(nameof(implementationType));
        }

        return Add(builder, serviceType, implementationType, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <paramref name="serviceType"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton(this IServiceProviderBuilder builder, Type serviceType, Func<IServiceProvider, object> implementationFactory)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return Add(builder, serviceType, implementationFactory, ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton<TService, TImplementation>(this IServiceProviderBuilder builder)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddSingleton(typeof(TService), typeof(TImplementation));
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <paramref name="serviceType"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register and the implementation to use.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton(this IServiceProviderBuilder builder, Type serviceType)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        return builder.AddSingleton(serviceType, serviceType);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <typeparamref name="TService"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton<TService>(this IServiceProviderBuilder builder)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddSingleton(typeof(TService));
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <typeparamref name="TService"/> with a
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton<TService>(this IServiceProviderBuilder builder, Func<IServiceProvider, TService> implementationFactory)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddSingleton(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <typeparamref name="TService"/> with an
    /// implementation type specified in <typeparamref name="TImplementation" /> using the
    /// factory specified in <paramref name="implementationFactory"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <typeparam name="TService">The type of the service to add.</typeparam>
    /// <typeparam name="TImplementation">The type of the implementation to use.</typeparam>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationFactory">The factory that creates the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton<TService, TImplementation>(this IServiceProviderBuilder builder, Func<IServiceProvider, TImplementation> implementationFactory)
        where TService : class
        where TImplementation : class, TService
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationFactory == null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        return builder.AddSingleton(typeof(TService), implementationFactory);
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <paramref name="serviceType"/> with an
    /// instance specified in <paramref name="implementationInstance"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="serviceType">The type of the service to register.</param>
    /// <param name="implementationInstance">The instance of the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton(this IServiceProviderBuilder builder, Type serviceType, object implementationInstance)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (serviceType == null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (implementationInstance == null)
        {
            throw new ArgumentNullException(nameof(implementationInstance));
        }

        var serviceDescriptor = new ServiceDescriptor(serviceType, implementationInstance);
        builder.Add(serviceDescriptor);
        return builder;
    }

    /// <summary>
    /// Adds a singleton service of the type specified in <typeparamref name="TService" /> with an
    /// instance specified in <paramref name="implementationInstance"/> to the
    /// specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <param name="implementationInstance">The instance of the service.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    /// <seealso cref="ServiceLifetime.Singleton"/>
    public static IServiceProviderBuilder AddSingleton<TService>(this IServiceProviderBuilder builder, TService implementationInstance)
        where TService : class
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (implementationInstance == null)
        {
            throw new ArgumentNullException(nameof(implementationInstance));
        }

        return builder.AddSingleton(typeof(TService), implementationInstance);
    }

    private static IServiceProviderBuilder Add(IServiceProviderBuilder builder, Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        var descriptor = new ServiceDescriptor(
            serviceType,
            implementationType,
            lifetime);

        builder.Add(descriptor);

        return builder;
    }
    private static IServiceProviderBuilder Add(IServiceProviderBuilder builder, Type serviceType, Func<IServiceProvider, object> implementationFactory, ServiceLifetime lifetime)
    {
        var descriptor = new ServiceDescriptor(
            serviceType,
            implementationFactory,
            lifetime);

        builder.Add(descriptor);

        return builder;
    }
}