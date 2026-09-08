using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EventHub;

/// <summary>
/// Defines the contract-only composition seam for an event hub application.
/// </summary>
public interface IEventHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Registers a host service with the event hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IEventHubApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Registers a host service factory with the event hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    IEventHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the event hub application.
    /// </summary>
    /// <returns>The configured event hub application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    new IEventHubApplication Build();
}
