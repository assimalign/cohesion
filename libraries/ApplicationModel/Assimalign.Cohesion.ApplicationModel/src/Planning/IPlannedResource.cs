namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A manifest resource that can produce a platform-neutral realization plan.
/// </summary>
public interface IPlannedResource : IManifestResource
{
    /// <summary>
    /// Gets the planner label written by <see cref="IApplicationBuilder.Build"/>.
    /// Resource-area planners override this with a full label such as
    /// <c>Database planner</c>; direct implementations inherit the explicit
    /// <see cref="GenericPlanner"/> fallback label.
    /// </summary>
    string PlannerName => nameof(GenericPlanner);

    /// <summary>Gets the deployer-owned planning overrides for this resource.</summary>
    IResourceOptions Options { get; }

    /// <summary>Creates the resource's platform-neutral realization plan.</summary>
    /// <param name="context">The immutable facts available to the resource planner.</param>
    /// <returns>The realization plan.</returns>
    ResourcePlan CreatePlan(PlanContext context);
}
