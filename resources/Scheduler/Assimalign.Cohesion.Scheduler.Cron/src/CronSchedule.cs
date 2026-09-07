using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Base class for schedules driven by a crontab expression. The Scheduler area is not started yet
/// (design item 31 scaffolds only its filler application); this type keeps the dormant Cron package
/// aligned with the area root's <see cref="Schedule{TContext}"/> contract until the Scheduler program
/// defines its schedule model.
/// </summary>
/// <typeparam name="TContext">The schedule context type.</typeparam>
public abstract class CronSchedule<TContext> : Schedule<TContext>
    where TContext : IScheduleContext
{
    /// <summary>
    /// Initializes the schedule against its provider.
    /// </summary>
    /// <param name="provider">The schedule provider that owns this schedule.</param>
    protected CronSchedule(IScheduleProvider provider)
        : base(provider)
    {
    }

    /// <inheritdoc/>
    /// <remarks>Dormant: the cron evaluation loop lands with the Scheduler program.</remarks>
    public sealed override Task RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
