using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

internal sealed class NotificationHubApplicationHost : Host<NotificationHubApplicationContext>, INotificationHubApplication
{
    private readonly NotificationHubApplicationContext _context;

    internal NotificationHubApplicationHost(
        NotificationHubApplicationOptions options,
        NotificationHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override NotificationHubApplicationContext Context => _context;

    async Task INotificationHubApplication.RunAsync(CancellationToken cancellationToken)
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
