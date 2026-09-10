using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

internal sealed class SchedulerApplicationHost : Host<SchedulerApplicationContext>, ISchedulerApplication
{
    private readonly SchedulerApplicationContext _context;

    internal SchedulerApplicationHost(
        SchedulerApplicationOptions options,
        SchedulerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override SchedulerApplicationContext Context => _context;

    ISchedulerApplicationContext ISchedulerApplication.Context => _context;

    Task ISchedulerApplication.RunAsync(CancellationToken cancellationToken) =>
        RunAsync(cancellationToken);
}
