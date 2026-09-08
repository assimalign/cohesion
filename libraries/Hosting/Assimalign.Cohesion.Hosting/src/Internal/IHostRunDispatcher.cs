using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

internal interface IHostRunDispatcher
{
    Task RunAsync(CancellationToken cancellationToken);
}
