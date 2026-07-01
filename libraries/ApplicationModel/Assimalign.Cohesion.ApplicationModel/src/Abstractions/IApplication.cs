using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A built, runnable application: a desired-state graph of resources together with
/// the gateway that realizes it. An application does not host anything itself — it
/// hands its <see cref="Model"/> to a gateway and asks the gateway to make it so.
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
    /// Hands <see cref="Model"/> to the configured gateway, starts realization, and
    /// blocks until <paramref name="cancellationToken"/> is signalled, then tears the
    /// application down gracefully.
    /// </summary>
    /// <param name="cancellationToken">Signals that the application should stop and tear down.</param>
    /// <returns>A task that completes once the application has fully stopped.</returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}
