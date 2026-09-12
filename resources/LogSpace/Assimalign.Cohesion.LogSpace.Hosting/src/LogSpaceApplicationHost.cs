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

    Task ILogSpaceApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
