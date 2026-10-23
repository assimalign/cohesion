using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// The health-check registry. Accumulates registrations, rejecting
/// duplicate names, and snapshots them into an immutable <see cref="IHealthCheckService"/> when
/// built.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HealthChecksBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HealthChecksBuilder : IHealthChecksBuilder
{
    private readonly List<HealthCheckRegistration> _registrations = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes an empty health-check registry.</summary>
    public HealthChecksBuilder() { }

    /// <inheritdoc />
    public IReadOnlyCollection<HealthCheckRegistration> Registrations => _registrations;

    /// <inheritdoc />
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

    /// <inheritdoc />
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
