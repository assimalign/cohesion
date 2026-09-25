using System;

using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Timer.Internal;

namespace Assimalign.Cohesion.Scheduler.Timer;

/// <summary>
/// Composition extensions for timer-triggered scheduler jobs.
/// </summary>
public static class SchedulerTimerExtensions
{
    extension(ISchedulerApplicationBuilder builder)
    {
        /// <summary>
        /// Binds a declared job to a fixed-delay timer schedule.
        /// </summary>
        /// <param name="interval">The delay before and between occurrences.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddTimerSchedule(
            TimeSpan interval,
            IScheduleJob job)
        {
            return AddTimerScheduleCore(builder, job.Name, interval, interval, job, null);
        }

        /// <summary>
        /// Binds a declared job to a named fixed-delay timer schedule.
        /// </summary>
        /// <param name="name">The schedule name.</param>
        /// <param name="interval">The delay before and between occurrences.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddTimerSchedule(
            string name,
            TimeSpan interval,
            IScheduleJob job)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return AddTimerScheduleCore(builder, name, interval, interval, job, null);
        }

        /// <summary>
        /// Binds a declared job to a named timer using explicit first-delay and clock values.
        /// </summary>
        /// <param name="name">The schedule name.</param>
        /// <param name="dueTime">The delay before the first occurrence.</param>
        /// <param name="interval">The delay measured after each completed occurrence.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <param name="timeProvider">The clock used for evaluation and delays.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddTimerSchedule(
            string name,
            TimeSpan dueTime,
            TimeSpan interval,
            IScheduleJob job,
            TimeProvider timeProvider)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(timeProvider);
            return AddTimerScheduleCore(builder, name, dueTime, interval, job, timeProvider);
        }
    }

    private static ISchedulerApplicationBuilder AddTimerScheduleCore(
        ISchedulerApplicationBuilder builder,
        string? name,
        TimeSpan dueTime,
        TimeSpan interval,
        IScheduleJob job,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        return builder.AddScheduleProvider(
            new TimerScheduleProvider(name, dueTime, interval, [job], timeProvider));
    }
}
