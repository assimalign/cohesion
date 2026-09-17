using System;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Composition extensions for cron-triggered scheduler jobs.
/// </summary>
public static class SchedulerCronExtensions
{
    extension(ISchedulerApplicationBuilder builder)
    {
        /// <summary>
        /// Binds a declared job to a five-field cron expression.
        /// </summary>
        /// <param name="expression">The cron expression.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddCronSchedule(
            string expression,
            IScheduleJob job)
        {
            return AddCronScheduleCore(builder, job.Name, Crontab.Parse(expression), job, null);
        }

        /// <summary>
        /// Binds a declared job to a named five-field cron schedule.
        /// </summary>
        /// <param name="name">The schedule name.</param>
        /// <param name="expression">The cron expression.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddCronSchedule(
            string name,
            string expression,
            IScheduleJob job)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return AddCronScheduleCore(builder, name, Crontab.Parse(expression), job, null);
        }

        /// <summary>
        /// Binds a declared job to a parsed cron schedule using a supplied clock.
        /// </summary>
        /// <param name="name">The schedule name.</param>
        /// <param name="expression">The parsed cron expression.</param>
        /// <param name="job">A job previously declared with <c>AddJob</c>.</param>
        /// <param name="timeProvider">The clock used for evaluation and delays.</param>
        /// <returns>The same builder for chaining.</returns>
        public ISchedulerApplicationBuilder AddCronSchedule(
            string name,
            Crontab expression,
            IScheduleJob job,
            TimeProvider timeProvider)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(timeProvider);
            return AddCronScheduleCore(builder, name, expression, job, timeProvider);
        }
    }

    private static ISchedulerApplicationBuilder AddCronScheduleCore(
        ISchedulerApplicationBuilder builder,
        string? name,
        Crontab expression,
        IScheduleJob job,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(job);

        return builder.AddScheduleProvider(
            new CronScheduleProvider(name, expression, [job], timeProvider));
    }
}
