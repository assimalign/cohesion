using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a type used to build application configuration.
/// </summary>
public interface IConfigurationBuilder
{
    /// <summary>
    /// Adds a configuration provider to be built.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns>The same instance of <see cref="IConfigurationBuilder"/>.</returns>
    IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure);

    /// <summary>
    /// Synchronously builds the <see cref="IConfigurationRoot"/> by loading 
    /// the providers. "<see cref="IConfigurationProvider.Load"/>"
    /// </summary>
    /// <returns>An <see cref="IConfigurationRoot"/> with keys and values from the registered sources.</returns>
    IConfigurationRoot Build();

    /// <summary>
    /// Asynchronously builds the <see cref="IConfigurationRoot"/> by loading 
    /// the providers. "<see cref="IConfigurationProvider.LoadAsync"/>"
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IConfigurationRoot> BuildAsync(CancellationToken cancellationToken = default);
}