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

    Task IApiManagerApplication.RunAsync(CancellationToken cancellationToken) =>
        base.RunAsync(cancellationToken);
}
