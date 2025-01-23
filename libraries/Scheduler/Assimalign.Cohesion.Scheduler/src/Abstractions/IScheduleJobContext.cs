using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

public interface IScheduleJobContext
{
    /// <summary>
    /// 
    /// </summary>
    DateTimeOffset LastRunTime { get; }

    /// <summary>
    /// 
    /// </summary>
    DateTimeOffset NextRunTime { get; }
}
