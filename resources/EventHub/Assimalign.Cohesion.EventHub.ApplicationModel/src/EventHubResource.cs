using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed EventHub workload in an application graph.</summary>
public sealed class EventHubResource : PlannedResource
{
    private const string EventHubPlannerName = "EventHub planner";

    /// <summary>Initializes a EventHub resource.</summary>
    /// <param name="manifest">The EventHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public EventHubResource(
        ResourceManifest manifest,
        EventHubResourceOptions? options = null)
        : base(manifest, options ?? new EventHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => EventHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return EventHubPlanner.CreatePlan(context);
    }
}
