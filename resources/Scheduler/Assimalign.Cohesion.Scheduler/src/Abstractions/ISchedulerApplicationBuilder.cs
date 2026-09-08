using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Defines the contract-only composition seam for a scheduler application.
/// </summary>
public interface ISchedulerApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the scheduler application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    ISchedulerApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the scheduler host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    ISchedulerApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the scheduler application.
    /// </summary>
    /// <returns>The configured scheduler application.</returns>
    new ISchedulerApplication Build();
}
