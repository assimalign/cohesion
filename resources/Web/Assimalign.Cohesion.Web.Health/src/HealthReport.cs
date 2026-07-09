using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Health;

/// <summary>
/// The aggregate result of running a set of health checks: the per-check
/// <see cref="Entries"/>, the worst-case aggregate <see cref="Status"/>, and the total
/// wall-clock <see cref="TotalDuration"/>.
/// </summary>
/// <remarks>
/// The aggregate <see cref="Status"/> is the minimum status across every entry (see
/// <see cref="HealthStatus"/> for the ordering). A report with no entries is
/// <see cref="HealthStatus.Healthy"/> — a liveness probe that runs no checks reports the
/// process as up.
/// </remarks>
public sealed class HealthReport
{
    /// <summary>
    /// A shared empty report (no entries, zero duration, <see cref="HealthStatus.Healthy"/>).
    /// </summary>
    public static readonly HealthReport Empty =
        new(new Dictionary<string, HealthReportEntry>(0), TimeSpan.Zero);

    /// <summary>
    /// Initializes a new <see cref="HealthReport"/> and computes its aggregate status from the
    /// supplied entries.
    /// </summary>
    /// <param name="entries">The per-check entries keyed by registration name.</param>
    /// <param name="totalDuration">The total wall-clock time spent running the checks.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entries"/> is <see langword="null"/>.</exception>
    public HealthReport(IReadOnlyDictionary<string, HealthReportEntry> entries, TimeSpan totalDuration)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries = entries;
        TotalDuration = totalDuration;
        Status = Aggregate(entries);
    }

    /// <summary>
    /// Gets the per-check entries keyed by registration name.
    /// </summary>
    public IReadOnlyDictionary<string, HealthReportEntry> Entries { get; }

    /// <summary>
    /// Gets the aggregate status: the worst (lowest) status across all <see cref="Entries"/>,
    /// or <see cref="HealthStatus.Healthy"/> when there are none.
    /// </summary>
    public HealthStatus Status { get; }

    /// <summary>
    /// Gets the total wall-clock time spent running the checks.
    /// </summary>
    public TimeSpan TotalDuration { get; }

    private static HealthStatus Aggregate(IReadOnlyDictionary<string, HealthReportEntry> entries)
    {
        HealthStatus worst = HealthStatus.Healthy;

        foreach (KeyValuePair<string, HealthReportEntry> entry in entries)
        {
            if (entry.Value.Status < worst)
            {
                worst = entry.Value.Status;
            }
        }

        return worst;
    }
}
