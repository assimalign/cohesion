using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Scheduler.Internal;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Composition extensions for delegate-backed scheduler jobs.
/// </summary>
public static class SchedulerJobExtensions
{
    extension(ISchedulerApplicationBuilder builder)
    {
        /// <summary>
        /// Declares delegate-backed work without assigning a trigger.
        /// </summary>
        /// <param name="name">The stable job name.</param>
        /// <param name="execute">The callback invoked for each bound occurrence.</param>
        /// <returns>The declared job, for binding to a Cron or Timer schedule.</returns>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="execute"/> is <see langword="null"/>.</exception>
        public IScheduleJob AddJob(
            string name,
            Func<IScheduleContext, CancellationToken, ValueTask> execute)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(execute);

            IScheduleJob job = new DelegateScheduleJob(JobId.New(), name, execute);
            builder.AddJob(job);
            return job;
        }
    }
}
