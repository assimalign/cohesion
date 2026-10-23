using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Health.Internal;

/// <summary>
/// The default <see cref="IHealthChecksBuilder"/>. Accumulates registrations, rejecting
/// duplicate names, and snapshots them into an immutable <see cref="HealthCheckService"/> when
/// built.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HealthChecksBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HealthCheckAlreadyRegistered, registration.Name));
        }

        _registrations.Add(registration);

        return this;
    }

    public IHealthCheckService Build() => new HealthCheckService(_registrations.ToArray());

    private sealed class DebugView
    {
        private readonly HealthChecksBuilder _target;

        public DebugView(HealthChecksBuilder value)
        {
            _target = value;
        }

        public IReadOnlyCollection<HealthCheckRegistration> Registrations => _target.Registrations;
    }
}
