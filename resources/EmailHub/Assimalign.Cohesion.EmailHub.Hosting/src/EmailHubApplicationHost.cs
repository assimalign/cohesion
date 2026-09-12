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

    Task IEmailHubApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
