using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed IoTHub workload in an application graph.</summary>
public sealed class IoTHubResource : PlannedResource
{
    private const string IoTHubPlannerName = "IoTHub planner";

    /// <summary>Initializes a IoTHub resource.</summary>
    /// <param name="manifest">The IoTHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public IoTHubResource(
        ResourceManifest manifest,
        IoTHubResourceOptions? options = null)
        : base(manifest, options ?? new IoTHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => IoTHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return IoTHubPlanner.CreatePlan(context);
    }
}
