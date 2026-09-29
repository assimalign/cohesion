using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The control plane that realizes an <see cref="IApplicationModel"/> on a target —
/// local processes, Docker, or Kubernetes. A gateway packages or gathers each resource's
/// artifact, provisions resources in dependency order gating on readiness, and supervises
/// them. Destructive teardown is a distinct <see cref="GatewayRunMode.Teardown"/> operation.
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
    /// Validates that this gateway can realize every resource plan in the model without
    /// contacting the target platform or gathering artifacts.
    /// </summary>
    /// <param name="model">The desired-state graph to validate.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// A resource plan cannot be realized by this gateway.
    /// </exception>
    void Validate(IApplicationModel model);

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
    /// Reconciles the model with the target platform. Re-entry is level-triggered: inputs
    /// are resolved again and each selected controller is invoked again without re-gating
    /// resources that already satisfied initial readiness.
    /// </summary>
    /// <param name="model">The desired-state graph to reconcile.</param>
    /// <param name="cancellationToken">Signals that reconciliation should be abandoned.</param>
    /// <returns>A task that completes once this reconcile pass finishes.</returns>
    Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops active supervision and releases runtime-scoped resources, honoring
    /// per-resource grace. Persistent platform objects remain; this operation is not
    /// destructive teardown.
    /// </summary>
    /// <param name="cancellationToken">Bounds how long supervision shutdown may take.</param>
    /// <returns>A task that completes once active supervision has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Destructively removes the platform objects realized for the model, in reverse
    /// dependency order. Unlike <see cref="StopAsync"/>, this is teardown.
    /// </summary>
    /// <param name="model">The desired-state graph whose realized objects are removed.</param>
    /// <param name="cancellationToken">Bounds how long teardown may take.</param>
    /// <returns>A task that completes once uninstall finishes.</returns>
    Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default);
}
