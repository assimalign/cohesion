using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface ISchedule
{
    /// <summary>
    /// A name for the schedule.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// A description of the schedule.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The collection of jobs to execute 
    /// </summary>
    IEnumerable<IScheduleJob> Jobs { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="job"></param>
    void Add(IScheduleJob job);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="job"></param>
    void Remove(IScheduleJob job);
}