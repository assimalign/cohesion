using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// One health contributor's transport-neutral result.
/// </summary>
/// <param name="Status">
/// The reported status. A default contribution is <see cref="ResourceHealthStatus.Unhealthy"/>, so
/// a result nobody set fails closed.
/// </param>
/// <param name="Description">An optional human-readable description.</param>
/// <param name="Data">
/// Optional diagnostic key/value pairs. Values are strings so a report serializes without
/// reflection.
/// </param>
public readonly record struct HealthContribution(
    ResourceHealthStatus Status,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    /// <summary>Creates a healthy contribution.</summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value pairs.</param>
    /// <returns>A healthy contribution.</returns>
    public static HealthContribution Healthy(string? description = null, IReadOnlyDictionary<string, string>? data = null)
    {
        return new HealthContribution(ResourceHealthStatus.Healthy, description, data);
    }

    /// <summary>Creates a degraded contribution.</summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value pairs.</param>
    /// <returns>A degraded contribution.</returns>
    public static HealthContribution Degraded(string? description = null, IReadOnlyDictionary<string, string>? data = null)
    {
        return new HealthContribution(ResourceHealthStatus.Degraded, description, data);
    }

    /// <summary>Creates an unhealthy contribution.</summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value pairs.</param>
    /// <returns>An unhealthy contribution.</returns>
    public static HealthContribution Unhealthy(string? description = null, IReadOnlyDictionary<string, string>? data = null)
    {
        return new HealthContribution(ResourceHealthStatus.Unhealthy, description, data);
    }
}
