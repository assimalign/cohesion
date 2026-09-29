using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Configures a manifest-less executable added through
/// <see cref="LocalGatewayExtensions.AddExecutable(IApplicationBuilder, ResourceName, string, Action{IExecutableResourceOptionsBuilder})"/>.
/// </summary>
public interface IExecutableResourceOptionsBuilder
{
    /// <summary>Uses a stdout substring as the executable's readiness signal.</summary>
    /// <param name="marker">The ordinal stdout substring.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="marker"/> is empty.</exception>
    IExecutableResourceOptionsBuilder UseReadyMarker(string marker);

    /// <summary>Uses a probe to determine initial readiness.</summary>
    /// <param name="probe">The readiness probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IExecutableResourceOptionsBuilder UseReadinessProbe(IProbeSpec probe);

    /// <summary>Uses a probe before readiness probing begins.</summary>
    /// <param name="probe">The startup probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IExecutableResourceOptionsBuilder UseStartupProbe(IProbeSpec probe);

    /// <summary>Uses a probe to monitor a running executable.</summary>
    /// <param name="probe">The liveness probe.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <see langword="null"/>.</exception>
    IExecutableResourceOptionsBuilder UseLivenessProbe(IProbeSpec probe);

    /// <summary>Sets the restart policy for exits and liveness failures.</summary>
    /// <param name="policy">The restart policy.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is undefined.</exception>
    IExecutableResourceOptionsBuilder UseRestartPolicy(RestartPolicy policy);

    /// <summary>Adds an endpoint that the local gateway allocates and injects.</summary>
    /// <param name="endpoint">The endpoint declaration.</param>
    /// <returns>This options builder.</returns>
    IExecutableResourceOptionsBuilder AddEndpoint(ResourceEndpoint endpoint);

    /// <summary>Adds an environment variable supplied to the executable.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The variable value.</param>
    /// <returns>This options builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    IExecutableResourceOptionsBuilder AddEnvironment(string name, string value);
}
