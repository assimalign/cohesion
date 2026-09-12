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

    Task IEventHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
