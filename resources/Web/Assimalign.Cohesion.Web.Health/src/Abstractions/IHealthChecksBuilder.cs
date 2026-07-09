using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// Accumulates <see cref="HealthCheckRegistration"/> entries at builder time and produces the
/// immutable <see cref="IHealthCheckService"/> that runs them.
/// </summary>
/// <remarks>
/// The builder is the single composition seam: registrations are added while the host is being
/// built and the check set is frozen when <see cref="Build"/> is called. There is no runtime
/// mutation surface — a running host cannot add or remove checks. Obtain a builder from
/// <see cref="HealthChecks.CreateBuilder"/> (or, in a hosted app, from the
/// <c>AddHealthChecks</c> DI extension in <c>Assimalign.Cohesion.Web.Health.Hosting</c>).
/// </remarks>
public interface IHealthChecksBuilder
{
    /// <summary>
    /// Gets the registrations added so far.
    /// </summary>
    IReadOnlyCollection<HealthCheckRegistration> Registrations { get; }

    /// <summary>
    /// Adds a registration to the builder.
    /// </summary>
    /// <param name="registration">The registration to add.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="registration"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">Thrown when a check with the same name was already added.</exception>
    IHealthChecksBuilder Add(HealthCheckRegistration registration);

    /// <summary>
    /// Builds the immutable <see cref="IHealthCheckService"/> from the current registrations.
    /// </summary>
    /// <returns>A service that snapshots the registrations added up to this call.</returns>
    IHealthCheckService Build();
}
