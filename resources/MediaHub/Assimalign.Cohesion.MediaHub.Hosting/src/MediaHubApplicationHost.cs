using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MediaHub;

namespace Assimalign.Cohesion.MediaHub.Hosting;

internal sealed class MediaHubApplicationHost : Host<MediaHubApplicationContext>, IMediaHubApplication
{
    private readonly MediaHubApplicationContext _context;

    internal MediaHubApplicationHost(
        MediaHubApplicationOptions options,
        MediaHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override MediaHubApplicationContext Context => _context;

    async Task IMediaHubApplication.RunAsync(CancellationToken cancellationToken)
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
