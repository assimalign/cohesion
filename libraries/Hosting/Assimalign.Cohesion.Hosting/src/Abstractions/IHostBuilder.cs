using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A builder pattern for creating a <see cref="IHost"/>.
/// </summary>
public interface IHostBuilder
{
    /// <summary>
    /// Adds a service to host.
    /// </summary>
    /// <param name="service">The service managed by the host.</param>
    /// <returns>The same instance of <see cref="IHostBuilder"/></returns>
    IHostBuilder AddHostedService(IHostService service);

    /// <summary>
    /// Adds a service to the host.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IHostBuilder AddHostedService(Func<IHostContext, IHostService> configure);

    /// <summary>
    /// Builds the <see cref="IHost"/>.
    /// </summary>
    /// <returns></returns>
    IHost Build();
}