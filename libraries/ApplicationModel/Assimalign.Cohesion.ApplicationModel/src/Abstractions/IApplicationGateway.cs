using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The control plane that realizes an <see cref="IApplicationModel"/> on a target —
/// local processes, Docker, or Kubernetes. A gateway packages or gathers each resource's
/// artifact, provisions resources in dependency order gating on readiness, supervises
/// them, and tears them down in reverse order.
/// </summary>
/// <remarks>
/// The base library ships no concrete gateway; implementations live in the
/// <c>Assimalign.Cohesion.ApplicationModel.Gateway[.{Platform}]</c> packages and are
/// selected explicitly via <see cref="IApplicationBuilder.UseGateway"/>.
/// </remarks>
public interface IApplicationGateway
{
    /// <summary>
    /// A stable gateway identity, for example <c>local</c>, <c>docker</c>, or <c>kubernetes</c>.
    /// </summary>
    ResourceName Name { get; }

    /// <summary>
    /// Realizes the model: gathers each resource's artifact, then provisions resources in
    /// topological order, gating each on its dependencies reaching a ready state. Completes
    /// once everything is running, or throws if a resource fails to become ready within its budget.
    /// </summary>
    /// <param name="model">The desired-state graph to realize.</param>
    /// <param name="cancellationToken">Signals that startup should be abandoned.</param>
    /// <returns>A task that completes once the model is fully realized.</returns>
    Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears the application down in reverse dependency order, honoring per-resource grace.
    /// </summary>
    /// <param name="cancellationToken">Bounds how long teardown may take.</param>
    /// <returns>A task that completes once the application has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
