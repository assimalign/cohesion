using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MediaHub;

/// <summary>
/// Defines the contract-only composition seam for a media hub application.
/// </summary>
public interface IMediaHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the media hub application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IMediaHubApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the media hub host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    IMediaHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the media hub application.
    /// </summary>
    /// <returns>The configured media hub application.</returns>
    new IMediaHubApplication Build();
}
