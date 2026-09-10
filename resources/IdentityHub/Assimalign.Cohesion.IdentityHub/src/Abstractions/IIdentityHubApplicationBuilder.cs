using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Defines the contract-only composition seam for an identity hub application.
/// </summary>
public interface IIdentityHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared.</exception>
    IIdentityHubApplicationBuilder AddAudience(string audience);

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered.</exception>
    IIdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure);

    /// <summary>
    /// Registers a host service with the identity hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IIdentityHubApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Registers a host service factory with the identity hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    IIdentityHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    /// <exception cref="InvalidOperationException">A registered host service factory returns <see langword="null"/>.</exception>
    new IIdentityHubApplication Build();
}
