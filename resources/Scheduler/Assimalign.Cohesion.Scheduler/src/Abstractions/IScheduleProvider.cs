using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface IScheduleProvider
{
    ///// <summary>
    ///// 
    ///// </summary>
    //void Start(ScheduleId id);

    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="id"></param>
    //void Stop(ScheduleId id);

    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="id"></param>
    //void Begin(ScheduleId id);

    ///// <summary>
    ///// 
    ///// </summary>
    //void End(ScheduleId id);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    ISchedule GetSchedule(ScheduleId id);

    /// <summary>
    /// Get all schedules within a given provider
    /// </summary>
    /// <returns></returns>
    IEnumerable<ISchedule> GetSchedules();

    /// <summary>
    /// Disableds a given job under a specific schedule.
    /// </summary>
    /// <param name="scheduleId"></param>
    /// <param name="jobId"></param>
    void DisableJob(ScheduleId scheduleId, JobId jobId);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="scheduleId"></param>
    /// <param name="jobId"></param>
    void EnableJob(ScheduleId scheduleId, JobId jobId);
}
