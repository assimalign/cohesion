namespace Assimalign.Cohesion.Health;

/// <summary>
/// Represents the health of a component or of an aggregate <see cref="HealthReport"/>.
/// </summary>
/// <remarks>
/// The numeric values are ordered from least to most healthy so the aggregate status of a
/// report is the <em>minimum</em> of its entries: <see cref="Unhealthy"/> (0) &lt;
/// <see cref="Degraded"/> (1) &lt; <see cref="Healthy"/> (2). An empty report aggregates to
/// <see cref="Healthy"/>. The ordering is part of the contract — do not renumber the members.
/// </remarks>
public enum HealthStatus
{
    /// <summary>
    /// The component is not functioning. In readiness/liveness terms the component is failing
    /// its probe and traffic should not be routed to it.
    /// </summary>
    Unhealthy = 0,

    /// <summary>
    /// The component is functioning but in a degraded state (for example, slow or partially
    /// available). It is still considered available for traffic but the condition is worth
    /// surfacing to operators. Mirrors <c>ResourceLifecycle.Degraded</c> in the orchestration plane.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// The component is functioning normally.
    /// </summary>
    Healthy = 2,
}
