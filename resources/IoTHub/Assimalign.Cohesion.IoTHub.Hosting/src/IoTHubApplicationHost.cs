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

    async Task IIoTHubApplication.RunAsync(CancellationToken cancellationToken)
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
