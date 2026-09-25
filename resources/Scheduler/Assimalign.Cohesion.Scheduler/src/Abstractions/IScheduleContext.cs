using System;
namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Describes one scheduled job occurrence.
/// </summary>
public interface IScheduleContext
{
    /// <summary>
    /// A unique identifier for the schedule.
    /// </summary>
    ScheduleId Id { get; }

    /// <summary>
    /// A friendly name for the schedule.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// A description of the schedule.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Gets the occurrence time currently being executed.
    /// </summary>
    DateTime ScheduledTime { get; }

    /// <summary>
    /// Gets the occurrence time that follows this occurrence.
    /// </summary>
    DateTime? NextRunTime { get; }

    /// <summary>
    /// Gets the preceding occurrence time, or <see langword="null"/> for the first occurrence.
    /// </summary>
    DateTime? LastRunTime { get; }
}
