using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// The value returned by an <see cref="IHealthCheck"/> describing the outcome of a single
/// evaluation.
/// </summary>
/// <remarks>
/// This is a lightweight, immutable value type so a check can return a result without
/// allocating on the healthy path. Use the <see cref="Healthy(string?, IReadOnlyDictionary{string, object}?)"/>,
/// <see cref="Degraded(string?, Exception?, IReadOnlyDictionary{string, object}?)"/>, and
/// <see cref="Unhealthy(string?, Exception?, IReadOnlyDictionary{string, object}?)"/> factory
/// members rather than the constructor for readable call sites.
/// </remarks>
public readonly struct HealthCheckResult
{
    private static readonly IReadOnlyDictionary<string, object> EmptyData =
        new Dictionary<string, object>(0);

    /// <summary>
    /// Initializes a new <see cref="HealthCheckResult"/>.
    /// </summary>
    /// <param name="status">The reported status.</param>
    /// <param name="description">An optional human-readable description of the outcome.</param>
    /// <param name="exception">An optional exception captured when the check failed.</param>
    /// <param name="data">Optional key/value diagnostic data to surface alongside the result.</param>
    public HealthCheckResult(
        HealthStatus status,
        string? description = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null)
    {
        Status = status;
        Description = description;
        Exception = exception;
        Data = data ?? EmptyData;
    }

    /// <summary>
    /// Gets the reported status.
    /// </summary>
    public HealthStatus Status { get; }

    /// <summary>
    /// Gets an optional human-readable description of the outcome.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets an optional exception captured when the check failed.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the key/value diagnostic data associated with the result. Never <see langword="null"/>;
    /// an empty dictionary is used when no data was supplied.
    /// </summary>
    public IReadOnlyDictionary<string, object> Data { get; }

    /// <summary>
    /// Creates a <see cref="HealthStatus.Healthy"/> result.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="data">Optional diagnostic data.</param>
    /// <returns>A healthy <see cref="HealthCheckResult"/>.</returns>
    public static HealthCheckResult Healthy(
        string? description = null,
        IReadOnlyDictionary<string, object>? data = null)
        => new(HealthStatus.Healthy, description, exception: null, data);

    /// <summary>
    /// Creates a <see cref="HealthStatus.Degraded"/> result.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="exception">An optional exception describing the degradation.</param>
    /// <param name="data">Optional diagnostic data.</param>
    /// <returns>A degraded <see cref="HealthCheckResult"/>.</returns>
    public static HealthCheckResult Degraded(
        string? description = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null)
        => new(HealthStatus.Degraded, description, exception, data);

    /// <summary>
    /// Creates an <see cref="HealthStatus.Unhealthy"/> result.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <param name="exception">An optional exception describing the failure.</param>
    /// <param name="data">Optional diagnostic data.</param>
    /// <returns>An unhealthy <see cref="HealthCheckResult"/>.</returns>
    public static HealthCheckResult Unhealthy(
        string? description = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null)
        => new(HealthStatus.Unhealthy, description, exception, data);
}
