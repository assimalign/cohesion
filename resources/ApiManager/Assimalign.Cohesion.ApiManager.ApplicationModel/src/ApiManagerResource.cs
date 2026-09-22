using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed ApiManager workload in an application graph.</summary>
public sealed class ApiManagerResource : PlannedResource
{
    private const string ApiManagerPlannerName = "ApiManager planner";

    /// <summary>Initializes a ApiManager resource.</summary>
    /// <param name="manifest">The ApiManager resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public ApiManagerResource(
        ResourceManifest manifest,
        ApiManagerResourceOptions? options = null)
        : base(manifest, options ?? new ApiManagerResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => ApiManagerPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return ApiManagerPlanner.CreatePlan(context);
    }
}
