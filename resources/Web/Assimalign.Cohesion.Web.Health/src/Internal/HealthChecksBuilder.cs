using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Health.Internal;

/// <summary>
/// The default <see cref="IHealthChecksBuilder"/>. Accumulates registrations, rejecting
/// duplicate names, and snapshots them into an immutable <see cref="HealthCheckService"/> when
/// built.
/// </summary>
internal sealed class HealthChecksBuilder : IHealthChecksBuilder
{
    private readonly List<HealthCheckRegistration> _registrations = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<HealthCheckRegistration> Registrations => _registrations;

    public IHealthChecksBuilder Add(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!_names.Add(registration.Name))
        {
            throw new InvalidOperationException(
                $"A health check named '{registration.Name}' has already been registered.");
        }

        _registrations.Add(registration);

        return this;
    }

    public IHealthCheckService Build() => new HealthCheckService(_registrations.ToArray());
}
