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

    Task IMessageHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
