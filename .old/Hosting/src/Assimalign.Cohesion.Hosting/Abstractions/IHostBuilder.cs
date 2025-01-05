using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A builder pattern for creating a <see cref="IHost"/>.
/// </summary>
public interface IHostBuilder
{
    /// <summary>
    /// Adds a service to host to be started.
    /// </summary>
    /// <param name="service">The service managed by the host.</param>
    /// <returns>The same instance of <see cref="IHostBuilder"/></returns>
    IHostBuilder AddService(IHostService service);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IHostBuilder AddService(Func<IHostContext, IHostService> configure);
    /// <summary>
    /// Adds a <see cref="IServiceProvider"/> to the host context.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    IHostBuilder AddServiceProvider(IServiceProvider serviceProvider);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    IHostBuilder AddServiceProvider(Func<IHostContext, IServiceProvider> serviceProvider);
    /// <summary>
    /// Builds the <see cref="IHost"/>.
    /// </summary>
    /// <returns></returns>
    IHost Build();
}