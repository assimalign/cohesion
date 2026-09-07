using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApiManager;

namespace Assimalign.Cohesion.ApiManager.Hosting;

internal sealed class ApiManagerApplicationHost : Host<ApiManagerApplicationContext>, IApiManagerApplication
{
    private readonly ApiManagerApplicationContext _context;

    internal ApiManagerApplicationHost(
        ApiManagerApplicationOptions options,
        ApiManagerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override ApiManagerApplicationContext Context => _context;

    async Task IApiManagerApplication.RunAsync(CancellationToken cancellationToken)
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
