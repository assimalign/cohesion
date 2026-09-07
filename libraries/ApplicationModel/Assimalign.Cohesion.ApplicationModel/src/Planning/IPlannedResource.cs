namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A manifest resource that can produce a platform-neutral realization plan.
/// </summary>
public interface IPlannedResource : IManifestResource
{
    /// <summary>Gets the deployer-owned planning overrides for this resource.</summary>
    IResourceOptions Options { get; }

    /// <summary>Creates the resource's platform-neutral realization plan.</summary>
    /// <param name="context">The immutable facts available to the resource planner.</param>
    /// <returns>The realization plan.</returns>
    ResourcePlan CreatePlan(PlanContext context);
}
