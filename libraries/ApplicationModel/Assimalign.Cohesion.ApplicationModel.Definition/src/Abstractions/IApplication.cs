using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A built application: the model of what its gateway deploys, and the gateway that runs it.
/// </summary>
/// <remarks>
/// An application does not host resource workloads, so it deliberately does not extend a host
/// abstraction. Its gateway deploys the model's resources and child gateways across processes,
/// containers, or pods that the application does not own.
/// </remarks>
public interface IApplication
{
    /// <summary>
    /// Gets the immutable model of what the application's gateway deploys.
    /// </summary>
    IApplicationModel Model { get; }

    /// <summary>
    /// Runs the application's gateway with its model.
    /// </summary>
    /// <param name="cancellationToken">Signals that the gateway should stop.</param>
    /// <returns>A task that completes when the gateway stops.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// The application has no gateway, or has already been run.
    /// </exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}
