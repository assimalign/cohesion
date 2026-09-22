using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed NotificationHub workload in an application graph.</summary>
public sealed class NotificationHubResource : PlannedResource
{
    private const string NotificationHubPlannerName = "NotificationHub planner";

    /// <summary>Initializes a NotificationHub resource.</summary>
    /// <param name="manifest">The NotificationHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public NotificationHubResource(
        ResourceManifest manifest,
        NotificationHubResourceOptions? options = null)
        : base(manifest, options ?? new NotificationHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => NotificationHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return NotificationHubPlanner.CreatePlan(context);
    }
}
