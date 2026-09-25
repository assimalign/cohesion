using System;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Defines the contract-only composition seam for a scheduler application.
/// </summary>
public interface ISchedulerApplicationBuilder
{
    /// <summary>
    /// Declares a job without scheduling it.
    /// </summary>
    /// <remarks>
    /// Cron and Timer feature packages bind declared jobs to providers separately. An unbound
    /// job remains dormant.
    /// </remarks>
    /// <param name="job">The job declaration to register.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A different job with the same identifier is registered.</exception>
    ISchedulerApplicationBuilder AddJob(IScheduleJob job);

    /// <summary>
    /// Adds a schedule provider to the scheduler application.
    /// </summary>
    /// <param name="provider">The provider whose schedules the host executes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The same provider instance is already registered.</exception>
    ISchedulerApplicationBuilder AddScheduleProvider(IScheduleProvider provider);

    /// <summary>
    /// Builds the scheduler application.
    /// </summary>
    /// <returns>The configured scheduler application.</returns>
    ISchedulerApplication Build();
}
