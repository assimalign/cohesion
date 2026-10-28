namespace Assimalign.Cohesion.Hosting.Health;

/// <summary>
/// Represents the health of a host component, ordered from least to most healthy.
/// </summary>
/// <remarks>
/// The numeric ordering is part of the contract so an aggregate can select its least healthy
/// contribution. Do not renumber these values.
/// </remarks>
public enum HealthStatus
{
    /// <summary>
    /// The component is not functioning and should be considered unavailable.
    /// </summary>
    Unhealthy = 0,

    /// <summary>
    /// The component is functioning with reduced capability or performance.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// The component is functioning normally.
    /// </summary>
    Healthy = 2
}
