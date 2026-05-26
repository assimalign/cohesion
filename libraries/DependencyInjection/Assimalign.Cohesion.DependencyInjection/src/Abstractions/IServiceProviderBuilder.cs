using System;

namespace Assimalign.Cohesion.DependencyInjection;

public interface IServiceProviderBuilder
{
    /// <summary>
    /// Represents the collection of services to be used within th IoC Container.
    /// </summary>
    IServiceContainer Services { get; }

    /// <summary>
    /// Adds a service to <see cref="IServiceProviderBuilder.Services"/>.
    /// </summary>
    /// <param name="serviceDescriptor"></param>
    /// <returns></returns>
    IServiceProviderBuilder Add(ServiceDescriptor serviceDescriptor);

    /// <summary>
    /// Creates a <see cref="IServiceProvider"/> containing services from the provided <see cref="IServiceContainer"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceContainer"/> containing service descriptors.</param>
    /// <returns>The <see cref="IServiceProvider"/>.</returns>
    IServiceProvider Build();
}
