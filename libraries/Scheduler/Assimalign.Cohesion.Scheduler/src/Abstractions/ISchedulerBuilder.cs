using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface ISchedulerBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="schedule"></param>
    /// <returns></returns>
    ISchedulerBuilder AddSchedule(ISchedule schedule);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<IScheduler> BuildAsync();
}
