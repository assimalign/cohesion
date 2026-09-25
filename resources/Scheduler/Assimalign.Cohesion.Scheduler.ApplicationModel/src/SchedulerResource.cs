using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed Scheduler workload in an application graph.</summary>
public sealed class SchedulerResource : PlannedResource
{
    private const string SchedulerPlannerName = "Scheduler planner";

    /// <summary>Initializes a Scheduler resource.</summary>
    /// <param name="manifest">The Scheduler resource manifest.</param>
    /// <param name="options">Optional singleton planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public SchedulerResource(
        ResourceManifest manifest,
        SchedulerResourceOptions? options = null)
        : base(manifest, options ?? new SchedulerResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => SchedulerPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return SchedulerPlanner.CreatePlan(context);
    }
}
