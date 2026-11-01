using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Configures how a gateway runs its model, and builds it: the infrastructure it deploys onto,
/// where it keeps its record, the parent gateway it joins, and its time limits.
/// </summary>
/// <remarks>
/// <see cref="ApplicationGatewayBuilder"/> implements it beside <see cref="IApplicationModelBuilder"/>, so a
/// gateway program declares its model and configures its gateway on one object, the way an area's
/// application builder implements its area's builder beside the host's.
/// </remarks>
public interface IApplicationGatewayBuilder
{
    /// <summary>
    /// Gets or sets how long every created or updated resource has to publish its first manifest once
    /// its deployment starts, and how long a child gateway waits for its parent's outputs; five
    /// minutes unless set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    TimeSpan StartupTimeout { get; set; }

    /// <summary>
    /// Gets or sets how long an instance or a child gateway stays registered without renewing;
    /// thirty seconds unless set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    TimeSpan LeaseDuration { get; set; }

    /// <summary>
    /// Sets the infrastructure the gateway deploys onto, which is also the channel its instances
    /// reach it through.
    /// </summary>
    /// <param name="infrastructure">The infrastructure provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UseInfrastructure(IApplicationGatewayInfrastructureProvider infrastructure);

    /// <summary>
    /// Sets how the gateway calls its instances' control planes: the instance client of the
    /// platform its instances run on. A gateway without one deploys and receives publications but
    /// cannot run an operator's action.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UseInstanceClient(IApplicationGatewayInstanceClient client);

    /// <summary>
    /// Sets the platform the gateway runs on, which <paramref name="factory"/> creates when the gateway
    /// is built: it is both the infrastructure the gateway deploys onto and the instance client it
    /// calls its instances with. The gateway owns it and disposes it when the gateway is disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="Build"/> calls the factory once, after everything else it checks has passed, and
    /// disposes the platform when the build fails after that. An infrastructure and an instance client
    /// set earlier are replaced; a later <see cref="UseInfrastructure"/> or
    /// <see cref="UseInstanceClient"/> replaces the platform in that role only. Objects given to those
    /// two methods are borrowed: the gateway never disposes them.
    /// </remarks>
    /// <typeparam name="TPlatform">The gateway half of the platform.</typeparam>
    /// <param name="factory">Creates the platform.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UsePlatform<TPlatform>(Func<TPlatform> factory)
        where TPlatform : IApplicationGatewayInfrastructureProvider, IApplicationGatewayInstanceClient;

    /// <summary>
    /// Sets where the gateway keeps its record, so a restarted gateway reconciles against it; the
    /// record stays in memory unless set.
    /// </summary>
    /// <param name="store">The record store.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UseStore(IApplicationGatewayStore store);

    /// <summary>
    /// Sets the parent gateway this one joins when it runs.
    /// </summary>
    /// <param name="parent">The parent, as this gateway reaches it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The parent has this gateway's name.</exception>
    /// <exception cref="InvalidOperationException">The gateway already joins a parent, or is built.</exception>
    IApplicationGatewayBuilder UseParent(IApplicationGatewayParent parent);

    /// <summary>
    /// Builds the gateway with its model. The builder accepts no changes afterward.
    /// </summary>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentException">The model breaks a rule; the message names the entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// No infrastructure provider or platform is set; the model names a parent the gateway has no
    /// connection to; the platform's factory returned <see langword="null"/>, or a platform that names
    /// no platform; or the gateway is already built.
    /// </exception>
    IApplicationGateway Build();
}
