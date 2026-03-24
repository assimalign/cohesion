using Assimalign.Cohesion.Scheduler;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

public class CronSchedule : Schedule
{
    public CronSchedule()
    {
        
    }

    public sealed override async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {

        }
    }
}
