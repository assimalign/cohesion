using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Scheduler;

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
    /// 
    /// </summary>
    DateTime NextRunTime { get; }

    /// <summary>
    /// 
    /// </summary>
    DateTime? LastRunTime { get; }
}
