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
    /// <param name="provider"></param>
    /// <returns></returns>
    IScheduleProvider AddScheduleProvider(IScheduleProvider provider);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task<IScheduler> BuildAsync();
}
