using System;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.NotificationHub;

/// <summary>
/// Defines the contract-only composition seam for a notification hub application.
/// </summary>
public interface INotificationHubApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Adds an existing host service to the notification hub application.
    /// </summary>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    INotificationHubApplicationBuilder AddService(IHostService service);

    /// <summary>
    /// Adds a host service factory that is materialized once for each build.
    /// </summary>
    /// <param name="factory">The factory to invoke with the notification hub host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    INotificationHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory);

    /// <summary>
    /// Builds the notification hub application.
    /// </summary>
    /// <returns>The configured notification hub application.</returns>
    new INotificationHubApplication Build();
}
