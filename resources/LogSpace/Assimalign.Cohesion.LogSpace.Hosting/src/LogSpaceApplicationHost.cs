using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class LogSpaceApplicationHost : Host<LogSpaceApplicationContext>, ILogSpaceApplication
{
    private readonly LogSpaceApplicationContext _context;

    internal LogSpaceApplicationHost(
        LogSpaceApplicationOptions options,
        LogSpaceApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override LogSpaceApplicationContext Context => _context;

    async Task ILogSpaceApplication.RunAsync(CancellationToken cancellationToken)
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
