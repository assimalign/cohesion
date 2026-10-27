using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Runs an application: deploys what its model declares onto an infrastructure provider, and
/// manages it while the gateway runs.
/// </summary>
/// <remarks>
/// This is the seam ApplicationModel.Gateway implements. It stays this small until that library
/// defines what a gateway exposes.
/// </remarks>
public interface IApplicationGateway
{
    /// <summary>
    /// Runs the gateway with an application's model.
    /// </summary>
    /// <param name="model">The model of what the gateway deploys.</param>
    /// <param name="cancellationToken">Signals that the gateway should stop.</param>
    /// <returns>A task that completes when the gateway stops.</returns>
    Task RunAsync(IApplicationModel model, CancellationToken cancellationToken = default);
}
