using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Acts as a host for schedules.
/// </summary>
public interface IScheduler : IDisposable
{
    /// <summary>
    /// Starts the scheduler 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
