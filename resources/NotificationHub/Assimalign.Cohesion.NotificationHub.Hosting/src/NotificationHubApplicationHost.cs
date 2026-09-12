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

    Task INotificationHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
