using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// One platform's implementation of workloads. A platform package ships one; it is a
/// cross-repository contract, so it stays small.
/// </summary>
/// <typeparam name="TExtension">The platform's extension hub type for workloads.</typeparam>
/// <remarks>
/// The revision, workload-hash, and template-version logic that decides create, update, or
/// nothing stays inside <see cref="ReconcileAsync"/>; the contract does not model it.
/// </remarks>
public interface IWorkloadDriver<TExtension> where TExtension : class
{
    /// <summary>
    /// Gets what the driver does with each portable field.
    /// </summary>
    WorkloadCapabilities Capabilities { get; }

    /// <summary>
    /// Validates a workload against the platform. Pure.
    /// </summary>
    /// <param name="workload">The workload.</param>
    /// <returns>The findings.</returns>
    FabricDiagnostics Validate(WorkloadView<TExtension> workload);

    /// <summary>
    /// Checks the live platform before the first apply of a generation: installed extensions,
    /// the ResourceManager's own permissions, and a server-side dry run where the platform has one.
    /// </summary>
    /// <param name="workload">The workload.</param>
    /// <param name="cancellationToken">A token that cancels the check.</param>
    /// <returns>The findings.</returns>
    ValueTask<FabricDiagnostics> PreflightAsync(WorkloadView<TExtension> workload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the workload to the platform and observes it.
    /// </summary>
    /// <param name="workload">The workload.</param>
    /// <param name="operation">The operation the apply reports under.</param>
    /// <param name="cancellationToken">A token that cancels the reconcile.</param>
    /// <returns>The observation.</returns>
    ValueTask<WorkloadObservation> ReconcileAsync(WorkloadView<TExtension> workload, FabricOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the workload's platform objects: only those labeled with its uid.
    /// </summary>
    /// <param name="workload">The workload.</param>
    /// <param name="cancellationToken">A token that cancels the release.</param>
    /// <returns>Whether the workload is released.</returns>
    ValueTask<ReleaseResult> ReleaseAsync(WorkloadView<TExtension> workload, CancellationToken cancellationToken = default);
}
