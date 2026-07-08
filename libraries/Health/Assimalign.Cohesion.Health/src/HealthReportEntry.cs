using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Health;

/// <summary>
/// The outcome of a single named health check within a <see cref="HealthReport"/>.
/// </summary>
/// <remarks>
/// An entry augments a check's returned <see cref="HealthCheckResult"/> with the measured
/// <see cref="Duration"/> and the registration's <see cref="Tags"/> so a report renderer can
/// present per-check timing and filtering metadata without re-reading the registry.
/// </remarks>
public readonly struct HealthReportEntry
{
    private static readonly IReadOnlyDictionary<string, object> EmptyData =
        new Dictionary<string, object>(0);

    private static readonly IReadOnlyCollection<string> EmptyTags = Array.Empty<string>();

    /// <summary>
    /// Initializes a new <see cref="HealthReportEntry"/>.
    /// </summary>
    /// <param name="status">The reported status of the check.</param>
    /// <param name="description">An optional human-readable description of the outcome.</param>
    /// <param name="duration">The wall-clock time the check took to run.</param>
    /// <param name="exception">An optional exception captured when the check failed.</param>
    /// <param name="data">Diagnostic data returned by the check.</param>
    /// <param name="tags">The tags the check was registered with.</param>
    public HealthReportEntry(
        HealthStatus status,
        string? description,
        TimeSpan duration,
        Exception? exception,
        IReadOnlyDictionary<string, object>? data,
        IReadOnlyCollection<string>? tags)
    {
        Status = status;
        Description = description;
        Duration = duration;
        Exception = exception;
        Data = data ?? EmptyData;
        Tags = tags ?? EmptyTags;
    }

    /// <summary>
    /// Gets the reported status of the check.
    /// </summary>
    public HealthStatus Status { get; }

    /// <summary>
    /// Gets an optional human-readable description of the outcome.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the wall-clock time the check took to run.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets an optional exception captured when the check failed.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the diagnostic data returned by the check. Never <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object> Data { get; }

    /// <summary>
    /// Gets the tags the check was registered with. Never <see langword="null"/>.
    /// </summary>
    public IReadOnlyCollection<string> Tags { get; }
}
