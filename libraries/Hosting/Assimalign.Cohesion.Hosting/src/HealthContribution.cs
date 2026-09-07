using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Describes one named host component's transport-neutral health result.
/// </summary>
/// <param name="Status">The reported health status.</param>
/// <param name="Description">An optional human-readable description.</param>
/// <param name="Data">Optional diagnostic key/value data.</param>
public readonly record struct HealthContribution(
    HealthStatus Status,
    string? Description = null,
    IReadOnlyDictionary<string, object>? Data = null)
{
    /// <summary>
    /// Creates a healthy contribution.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value data.</param>
    /// <returns>A healthy contribution.</returns>
    public static HealthContribution Healthy(
        string? description = null,
        IReadOnlyDictionary<string, object>? data = null)
    {
        return new HealthContribution(HealthStatus.Healthy, description, data);
    }

    /// <summary>
    /// Creates a degraded contribution.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value data.</param>
    /// <returns>A degraded contribution.</returns>
    public static HealthContribution Degraded(
        string? description = null,
        IReadOnlyDictionary<string, object>? data = null)
    {
        return new HealthContribution(HealthStatus.Degraded, description, data);
    }

    /// <summary>
    /// Creates an unhealthy contribution.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic key/value data.</param>
    /// <returns>An unhealthy contribution.</returns>
    public static HealthContribution Unhealthy(
        string? description = null,
        IReadOnlyDictionary<string, object>? data = null)
    {
        return new HealthContribution(HealthStatus.Unhealthy, description, data);
    }
}
