using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

public abstract class CronScheduleJob : IScheduleJob
{
    public JobId Id => throw new NotImplementedException();

    public ValueTask ExecuteAsync(IScheduleJobContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
