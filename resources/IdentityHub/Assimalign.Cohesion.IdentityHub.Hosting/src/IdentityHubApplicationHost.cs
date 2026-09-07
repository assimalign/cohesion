using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubApplicationHost : Host<IdentityHubApplicationContext>, IIdentityHubApplication
{
    private readonly IdentityHubApplicationContext _context;

    internal IdentityHubApplicationHost(
        IdentityHubApplicationOptions options,
        IdentityHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override IdentityHubApplicationContext Context => _context;

    async Task IIdentityHubApplication.RunAsync(CancellationToken cancellationToken)
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
