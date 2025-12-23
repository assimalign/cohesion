using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface IScheduleJob
{
    /// <summary>
    /// A unique identifier for the job.
    /// </summary>
    JobId Id { get; }

    /// <summary>
    /// A friendly name for the job.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// 
    /// </summary>
    JobState State { get; }

    /// <summary>
    /// Executes the job
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync(IScheduleContext context, CancellationToken cancellationToken = default);
}