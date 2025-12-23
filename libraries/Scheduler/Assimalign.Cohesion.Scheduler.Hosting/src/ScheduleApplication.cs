using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Scheduler;

using Assimalign.Cohesion.Hosting;

public class ScheduleApplication : Host<ScheduleContext>, IScheduler
{
    public ScheduleApplication(ScheduleOptions options) : base(options)
    {

    }
    public sealed override ScheduleContext Context => throw new System.NotImplementedException();

    Task IScheduler.StartAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StartAsync(cancellationToken);
    }
    Task IScheduler.StopAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StopAsync(cancellationToken);
    }
}

