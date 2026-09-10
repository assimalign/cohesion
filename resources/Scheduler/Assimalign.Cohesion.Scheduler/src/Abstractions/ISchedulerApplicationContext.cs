using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Describes the immutable runtime composition of a scheduler application.
/// </summary>
public interface ISchedulerApplicationContext : IHostContext
{
    /// <summary>
    /// Gets every declared job, including jobs not bound to a schedule.
    /// </summary>
    IReadOnlyList<IScheduleJob> Jobs { get; }

    /// <summary>
    /// Gets the schedule providers executed by the application.
    /// </summary>
    IReadOnlyList<IScheduleProvider> ScheduleProviders { get; }
}
