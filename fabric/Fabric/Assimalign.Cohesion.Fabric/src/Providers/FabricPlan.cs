using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What a provider would create for a spec in an environment, before anything exists: the
/// children and the order they start in. What-if prints it.
/// </summary>
/// <param name="Children">The private children.</param>
/// <param name="Stages">The start order: each stage lists child suffixes that may start together, and a stage starts once the previous one is ready.</param>
public sealed record FabricPlan(IReadOnlyList<ChildResource> Children, IReadOnlyList<IReadOnlyList<ResourceName>> Stages)
{
    /// <summary>
    /// Gets the plan of a resource that has no children.
    /// </summary>
    public static FabricPlan Empty { get; } = new([], []);
}
