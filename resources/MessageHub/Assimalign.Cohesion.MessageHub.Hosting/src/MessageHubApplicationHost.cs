using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MessageHub;

namespace Assimalign.Cohesion.MessageHub.Hosting;

internal sealed class MessageHubApplicationHost : Host<MessageHubApplicationContext>, IMessageHubApplication
{
    private readonly MessageHubApplicationContext _context;

    internal MessageHubApplicationHost(
        MessageHubApplicationOptions options,
        MessageHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    public override MessageHubApplicationContext Context => _context;

    async Task IMessageHubApplication.RunAsync(CancellationToken cancellationToken)
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
