using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EmailHub;

namespace Assimalign.Cohesion.EmailHub.Hosting;

internal sealed class EmailHubApplicationHost : Host<EmailHubApplicationContext>, IEmailHubApplication
{
    private readonly EmailHubApplicationContext _context;

    internal EmailHubApplicationHost(
        EmailHubApplicationOptions options,
        EmailHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override EmailHubApplicationContext Context => _context;

    async Task IEmailHubApplication.RunAsync(CancellationToken cancellationToken)
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
