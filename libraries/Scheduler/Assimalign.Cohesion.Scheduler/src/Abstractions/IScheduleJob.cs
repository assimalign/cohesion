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
    string Id { get; }

    /// <summary>
    /// Executes the job
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync(IScheduleJobContext context , CancellationToken cancellationToken = default);
}