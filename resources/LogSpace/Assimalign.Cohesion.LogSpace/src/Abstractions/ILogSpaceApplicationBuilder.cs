using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LogSpace;

/// <summary>
/// Defines the contract-only composition seam for a LogSpace application.
/// </summary>
public interface ILogSpaceApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Registers a host service with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    ILogSpaceApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Registers a host service factory with the LogSpace application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    ILogSpaceApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the LogSpace application.
    /// </summary>
    /// <returns>The configured LogSpace application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    new ILogSpaceApplication Build();
}
