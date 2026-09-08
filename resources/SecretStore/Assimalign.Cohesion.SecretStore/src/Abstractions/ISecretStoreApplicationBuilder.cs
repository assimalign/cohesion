using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.SecretStore;

/// <summary>
/// Defines the contract-only composition seam for a secret store application.
/// </summary>
public interface ISecretStoreApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the secret store application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    ISecretStoreApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the secret store host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    ISecretStoreApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the secret store application.
    /// </summary>
    /// <returns>The configured secret store application.</returns>
    new ISecretStoreApplication Build();
}
