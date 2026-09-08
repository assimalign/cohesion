using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Rezolvr;

/// <summary>
/// Defines the contract-only composition seam for a Rezolvr application.
/// </summary>
public interface IRezolvrApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the resolver application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    IRezolvrApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the resolver host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    IRezolvrApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the Rezolvr application.
    /// </summary>
    /// <returns>The configured Rezolvr application.</returns>
    new IRezolvrApplication Build();
}
