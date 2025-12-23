using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface ISchedule
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
    /// 
    /// </summary>
    int Retries { get; }

    /// <summary>
    /// 
    /// </summary>
    int[] RetryIntervals { get; }

    /// <summary>
    /// 
    /// </summary>
    int RetryCount { get; }

    /// <summary>
    /// 
    /// </summary>
    DateTime GetNextRunTime { get; }

    /// <summary>
    /// 
    /// </summary>
    DateTime GetLastRuntTime { get; }

    /// <summary>
    /// 
    /// </summary>
    ScheduleStatus Status { get; }

    /// <summary>
    /// 
    /// </summary>
    SchedulePriority Priority { get; }

    /// <summary>
    /// The collection of jobs to execute 
    /// </summary>
    IEnumerable<IScheduleJob> Jobs { get; }
}