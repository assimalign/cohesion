using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The one contract every provider implements: a fabric area provider (Workloads, Networking,
/// Storage), a service provider (IdentityHub, Database), or a platform-native provider.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> and <see cref="Plan"/> are pure, so the AppHost runs them at emit and
/// what-if shows their result before anything exists. The asynchronous members are
/// level-triggered and idempotent: they observe, act, and report, and never write the store.
/// A service provider never contains platform code; it produces fabric children.
/// </remarks>
public interface IResourceProvider
{
    /// <summary>
    /// Gets the types and actions the provider serves.
    /// </summary>
    ResourceProviderManifest Manifest { get; }

    /// <summary>
    /// Validates a resource's spec. Pure.
    /// </summary>
    /// <param name="resource">The resource, references unresolved.</param>
    /// <param name="context">The platforms and, at admission, the environment to validate against.</param>
    /// <returns>The findings.</returns>
    FabricDiagnostics Validate(ResourceView resource, FabricValidationContext context);

    /// <summary>
    /// Computes the children and start order for a spec in an environment. Pure.
    /// </summary>
    /// <param name="resource">The resource, references unresolved.</param>
    /// <param name="environment">The environment.</param>
    /// <returns>The plan.</returns>
    FabricPlan Plan(ResourceView resource, FabricEnvironment environment);

    /// <summary>
    /// Converges the platform toward the resource's spec and reports what it observed.
    /// </summary>
    /// <param name="context">What the reconcile observes.</param>
    /// <param name="cancellationToken">A token that cancels the reconcile.</param>
    /// <returns>The observation.</returns>
    ValueTask<ReconcileResult> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases what the resource owns before it is deleted.
    /// </summary>
    /// <param name="context">What the release observes.</param>
    /// <param name="cancellationToken">A token that cancels the release.</param>
    /// <returns>Whether the resource is released.</returns>
    ValueTask<ReleaseResult> ReleaseAsync(ReconcileContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one of the provider's declared actions.
    /// </summary>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">A token that cancels the action.</param>
    /// <returns>The outcome.</returns>
    ValueTask<ActionResult> InvokeActionAsync(ActionContext context, CancellationToken cancellationToken = default);
}
