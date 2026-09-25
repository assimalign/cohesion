using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed MediaHub workload in an application graph.</summary>
public sealed class MediaHubResource : PlannedResource
{
    private const string MediaHubPlannerName = "MediaHub planner";

    /// <summary>Initializes a MediaHub resource.</summary>
    /// <param name="manifest">The MediaHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public MediaHubResource(
        ResourceManifest manifest,
        MediaHubResourceOptions? options = null)
        : base(manifest, options ?? new MediaHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => MediaHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return MediaHubPlanner.CreatePlan(context);
    }
}
