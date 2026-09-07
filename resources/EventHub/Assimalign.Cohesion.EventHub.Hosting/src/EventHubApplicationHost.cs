using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EventHub;

namespace Assimalign.Cohesion.EventHub.Hosting;

internal sealed class EventHubApplicationHost : Host<EventHubApplicationContext>, IEventHubApplication
{
    private readonly EventHubApplicationContext _context;

    internal EventHubApplicationHost(
        EventHubApplicationOptions options,
        EventHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override EventHubApplicationContext Context => _context;

    async Task IEventHubApplication.RunAsync(CancellationToken cancellationToken)
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
