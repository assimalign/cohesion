using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting;

internal sealed class IoTHubApplicationHost : Host<IoTHubApplicationContext>, IIoTHubApplication
{
    private readonly IoTHubApplicationContext _context;

    internal IoTHubApplicationHost(
        IoTHubApplicationOptions options,
        IoTHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override IoTHubApplicationContext Context => _context;

    Task IIoTHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
