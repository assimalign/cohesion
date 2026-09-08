using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore;

/// <summary>
/// Defines the contract-only composition seam for a configuration store application.
/// </summary>
public interface IConfigurationStoreApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Registers a host service with the configuration store application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IConfigurationStoreApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Registers a host service factory with the configuration store application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    IConfigurationStoreApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the configuration store application.
    /// </summary>
    /// <returns>The configured configuration store application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    new IConfigurationStoreApplication Build();
}
