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

    async Task ISchedulerApplication.RunAsync(CancellationToken cancellationToken)
    {
        // TODO(design item 12): Route RunAsync through ResourceRuntime once the ambient runtime seam exists.
        if (cancellationToken.IsCancellationRequested)
        {
            await ((IHost)this).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IHost)this).StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await base.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
