namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Describes the availability of an observed Database component.</summary>
public enum DatabaseHealthStatus
{
    /// <summary>The component is unavailable, or readiness has not been established.</summary>
    Unhealthy = 0,

    /// <summary>The component remains available with an outstanding condition.</summary>
    Degraded = 1,

    /// <summary>The component has no observed health condition.</summary>
    Healthy = 2,
}
