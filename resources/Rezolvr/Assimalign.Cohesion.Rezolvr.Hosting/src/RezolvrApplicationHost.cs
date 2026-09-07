using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrApplicationHost : Host<RezolvrApplicationContext>, IRezolvrApplication
{
    private readonly RezolvrApplicationContext _context;

    internal RezolvrApplicationHost(
        RezolvrApplicationOptions options,
        RezolvrApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override RezolvrApplicationContext Context => _context;

    async Task IRezolvrApplication.RunAsync(CancellationToken cancellationToken)
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
