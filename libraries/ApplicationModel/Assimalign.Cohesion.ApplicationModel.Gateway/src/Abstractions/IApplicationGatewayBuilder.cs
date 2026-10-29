using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Configures how a gateway runs its model, and builds it: the infrastructure it deploys onto,
/// where it keeps its record, the parent gateway it joins, and its time limits.
/// </summary>
/// <remarks>
/// <see cref="ApplicationBuilder"/> implements it beside <see cref="IApplicationModelBuilder"/>, so a
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
    /// Sets the infrastructure the gateway deploys onto.
    /// </summary>
    /// <param name="infrastructure">The infrastructure provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UseInfrastructure(IInfrastructureProvider infrastructure);

    /// <summary>
    /// Adds a transport the gateway's instances reach it through. A gateway may have several, and
    /// it starts each when it runs.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The transport is already added.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    IApplicationGatewayBuilder UseTransport(IApplicationGatewayTransport transport);

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
    IApplicationGatewayBuilder UseParent(IApplicationParentGateway parent);

    /// <summary>
    /// Builds the gateway with its model. The builder accepts no changes afterward.
    /// </summary>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentException">The model breaks a rule; the message names the entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// No infrastructure provider is set; the model names a parent the gateway has no connection to;
    /// or the gateway is already built.
    /// </exception>
    IApplicationGateway Build();
}
