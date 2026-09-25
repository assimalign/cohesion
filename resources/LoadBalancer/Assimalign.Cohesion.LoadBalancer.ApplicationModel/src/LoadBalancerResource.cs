using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed LoadBalancer workload in an application graph.</summary>
public sealed class LoadBalancerResource : PlannedResource
{
    private const string LoadBalancerPlannerName = "LoadBalancer planner";

    /// <summary>Initializes a LoadBalancer resource.</summary>
    /// <param name="manifest">The LoadBalancer resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public LoadBalancerResource(
        ResourceManifest manifest,
        LoadBalancerResourceOptions? options = null)
        : base(manifest, options ?? new LoadBalancerResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => LoadBalancerPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return LoadBalancerPlanner.CreatePlan(context);
    }
}
