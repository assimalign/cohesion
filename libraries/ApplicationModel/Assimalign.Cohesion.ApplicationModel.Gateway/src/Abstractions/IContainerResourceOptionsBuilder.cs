using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Configures an opaque, digest-pinned container resource added through
/// <see cref="ApplicationGatewayResourceExtensions.AddContainer(IApplicationBuilder, ResourceName, string, Action{IContainerResourceOptionsBuilder})"/>.
/// </summary>
public interface IContainerResourceOptionsBuilder
{
    /// <summary>Uses a probe to determine initial readiness.</summary>
    /// <param name="probe">The readiness probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IContainerResourceOptionsBuilder UseReadinessProbe(IProbeSpec probe);

    /// <summary>Uses a probe before readiness probing begins.</summary>
    /// <param name="probe">The startup probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IContainerResourceOptionsBuilder UseStartupProbe(IProbeSpec probe);

    /// <summary>Uses a probe to monitor a running container.</summary>
    /// <param name="probe">The liveness probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IContainerResourceOptionsBuilder UseLivenessProbe(IProbeSpec probe);

    /// <summary>Sets the restart policy for exits and liveness failures.</summary>
    /// <param name="policy">The restart policy.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is undefined.</exception>
    IContainerResourceOptionsBuilder UseRestartPolicy(RestartPolicy policy);

    /// <summary>Adds an endpoint exposed by the container.</summary>
    /// <param name="endpoint">The endpoint declaration, including its container port.</param>
    /// <returns>This options builder.</returns>
    IContainerResourceOptionsBuilder AddEndpoint(ResourceEndpoint endpoint);

    /// <summary>Adds an environment variable supplied to the container.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The variable value.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or already declared.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    IContainerResourceOptionsBuilder AddEnvironment(string name, string value);
}
