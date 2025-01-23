using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// 
/// </summary>
public interface IScheduleTrigger
{
    /// <summary>
    /// 
    /// </summary>
    ISchedule Schedule { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task InvokeAsync(CancellationToken cancellationToken = default);
}