namespace Assimalign.Cohesion.Scheduler;

public enum SchedulePriority
{
    /// <summary>
    /// Long running schedules are executed in a separate thread.
    /// Using Default TaskScheduler.
    /// </summary>
    LongRunning,
    /// <summary>
    /// High Priority schedules are executed first.
    /// Using TickerTaskScheduler
    /// </summary>
    High,
    /// <summary>
    /// Normal Priority Tasks are executed after high priority tasks.
    /// Using TickerTaskScheduler
    /// </summary>
    Normal,
    /// <summary>
    /// Low Priority Tasks are executed last.
    /// Using TickerTaskScheduler
    /// </summary>
    Low
}
