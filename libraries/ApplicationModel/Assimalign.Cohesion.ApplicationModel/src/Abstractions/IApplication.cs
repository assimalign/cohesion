using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A built application operation: a desired-state graph of resources together with
/// the gateway selected for operations that require platform contact. An application
/// does not host resource workloads itself.
/// </summary>
/// <remarks>
/// <see cref="IApplication"/> deliberately does NOT extend a host abstraction. A host
/// runs inside a single process; an application is <em>described</em> and then
/// <em>realized</em> by an <see cref="IApplicationGateway"/> across potentially many
/// processes, containers, or pods that the application does not own.
/// </remarks>
public interface IApplication
{
    /// <summary>
    /// The immutable desired-state resource graph this application realizes.
    /// </summary>
    IApplicationModel Model { get; }

    /// <summary>
    /// Executes the operation selected by <see cref="IApplicationModel.RunMode"/>.
    /// <see cref="GatewayRunMode.Run"/> realizes and supervises through the selected
    /// gateway until cancellation, then releases supervision gracefully without
    /// interpreting cancellation as teardown. <see cref="GatewayRunMode.Describe"/>
    /// writes the model document without contacting the platform. <see cref="GatewayRunMode.Render"/>
    /// and <see cref="GatewayRunMode.Bootstrap"/> dispatch through optional capabilities on the
    /// selected gateway and write their platform representation to standard output.
    /// </summary>
    /// <param name="cancellationToken">Signals that the selected operation should stop.</param>
    /// <returns>A task that completes once the selected operation has completed.</returns>
    /// <exception cref="System.NotSupportedException">
    /// The selected gateway does not implement the requested optional operation.
    /// </exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}
