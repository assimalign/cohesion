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

    Task IMediaHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
