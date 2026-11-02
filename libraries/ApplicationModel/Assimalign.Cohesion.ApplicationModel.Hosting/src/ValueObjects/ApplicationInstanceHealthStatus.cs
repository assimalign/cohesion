namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The health a contributor reports.
/// </summary>
/// <remarks>
/// <see cref="Unhealthy"/> is the zero value, so a status nobody set fails closed.
/// </remarks>
public enum ApplicationInstanceHealthStatus
{
    /// <summary>The component cannot do its work.</summary>
    Unhealthy,

    /// <summary>The component works, with reduced capacity or quality.</summary>
    Degraded,

    /// <summary>The component works normally.</summary>
    Healthy,
}
