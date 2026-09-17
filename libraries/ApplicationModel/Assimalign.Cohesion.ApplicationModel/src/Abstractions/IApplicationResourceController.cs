using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A plan-driven, level-triggered reconciler for one resource: it compiles the desired platform
/// objects for a resource and applies them, then returns. It is the intermediary that
/// translates an <see cref="IApplicationResource"/> into platform-native reality.
/// </summary>
/// <remarks>
/// A controller does not own steady-state observation — that is the gateway's single
/// informer (see <see cref="IApplicationResourceStateManager"/>). <see cref="ReconcileAsync"/>
/// is idempotent: it is called on first deploy and on every reconcile tick, and must be
/// safe to re-run. A gateway routes each resource to the first registered controller whose
/// <see cref="CanRealize"/> returns <see langword="true"/>.
/// </remarks>
public interface IApplicationResourceController
{
    /// <summary>
    /// Indicates whether this controller can compile and realize the platform-neutral plan.
    /// This check must be pure and must not contact the platform or gather an artifact.
    /// </summary>
    /// <param name="plan">The validated resource plan to test.</param>
    /// <param name="reason">A human-readable refusal reason when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> if this controller can realize <paramref name="plan"/>.</returns>
    bool CanRealize(ResourcePlan plan, out string? reason);

    /// <summary>
    /// Computes the desired platform objects for the resource and applies them. Idempotent
    /// and non-blocking: it applies and returns; it does not wait for readiness (the gateway
    /// gates on the state manager).
    /// </summary>
    /// <param name="context">The resource, its model, its dependencies, and the state manager.</param>
    /// <param name="cancellationToken">Signals that reconciliation should be abandoned.</param>
    /// <returns>A task that completes once the desired objects have been applied.</returns>
    Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops runtime-scoped activity for the resource while retaining persistent platform
    /// objects so a later reconcile can resume them.
    /// </summary>
    /// <param name="context">The resource control context from the latest reconcile.</param>
    /// <param name="cancellationToken">Bounds how long runtime shutdown may take.</param>
    /// <returns>A task that completes once runtime-scoped activity has stopped.</returns>
    Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the resource's realized objects — the reverse of <see cref="ReconcileAsync"/>.
    /// </summary>
    /// <param name="context">The resource, its model, its dependencies, and the state manager.</param>
    /// <param name="cancellationToken">Bounds how long deletion may take.</param>
    /// <returns>A task that completes once the resource's objects have been removed.</returns>
    Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default);
}
