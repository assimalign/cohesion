using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What one reconcile observes.
/// </summary>
/// <param name="Resource">The resource, with its references resolved.</param>
/// <param name="Environment">The environment it runs in.</param>
/// <param name="Operation">The operation the reconcile reports under.</param>
/// <param name="Children">The resource's private children as currently stored, with their status.</param>
public sealed record ReconcileContext(
    ResourceView Resource,
    FabricEnvironment Environment,
    FabricOperation Operation,
    IReadOnlyList<ResourceEnvelope> Children);
