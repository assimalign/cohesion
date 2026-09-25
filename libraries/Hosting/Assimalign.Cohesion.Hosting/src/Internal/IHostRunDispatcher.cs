using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal interface IHostRunDispatcher
{
    Task RunAsync(CancellationToken cancellationToken);
}
