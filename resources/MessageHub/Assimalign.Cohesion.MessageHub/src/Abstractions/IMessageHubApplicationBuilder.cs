using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MessageHub;

/// <summary>
/// Defines the contract-only composition seam for a message hub application.
/// </summary>
public interface IMessageHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the message hub application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IMessageHubApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the message hub host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    IMessageHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the message hub application.
    /// </summary>
    /// <returns>The configured message hub application.</returns>
    new IMessageHubApplication Build();
}
