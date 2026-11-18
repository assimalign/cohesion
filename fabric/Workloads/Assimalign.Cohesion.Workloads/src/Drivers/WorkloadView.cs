using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// The view of one workload a platform's driver reconciles: the portable spec, with references
/// resolved, and the driver's own platform block, already read into its hub type.
/// </summary>
/// <typeparam name="TExtension">The platform's extension hub type.</typeparam>
/// <param name="Resource">The workload resource.</param>
/// <param name="Spec">The portable spec.</param>
/// <param name="Extension">The platform's block, or <see langword="null"/> when the workload has none.</param>
/// <param name="Environment">The environment the workload runs in.</param>
public sealed record WorkloadView<TExtension>(
    ResourceView Resource,
    WorkloadSpec Spec,
    TExtension? Extension,
    FabricEnvironment Environment) where TExtension : class;
