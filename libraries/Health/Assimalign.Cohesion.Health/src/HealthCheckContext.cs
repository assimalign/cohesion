using System;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// The context passed to an <see cref="IHealthCheck"/> when it runs. Carries the
/// <see cref="Registration"/> that produced the check so an implementation can read its own
/// name, tags, timeout, and failure-status policy.
/// </summary>
public sealed class HealthCheckContext
{
    /// <summary>
    /// Initializes a new <see cref="HealthCheckContext"/>.
    /// </summary>
    /// <param name="registration">The registration the running check was resolved from.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registration"/> is <see langword="null"/>.</exception>
    public HealthCheckContext(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        Registration = registration;
    }

    /// <summary>
    /// Gets the registration the running check was resolved from.
    /// </summary>
    public HealthCheckRegistration Registration { get; }
}
