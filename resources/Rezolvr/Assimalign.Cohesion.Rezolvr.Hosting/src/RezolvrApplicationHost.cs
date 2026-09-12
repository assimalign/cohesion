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

    Task IRezolvrApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
