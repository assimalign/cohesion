using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed LogSpace workload in an application graph.</summary>
public sealed class LogSpaceResource : PlannedResource
{
    private const string LogSpacePlannerName = "LogSpace planner";

    /// <summary>Initializes a LogSpace resource.</summary>
    /// <param name="manifest">The LogSpace resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public LogSpaceResource(
        ResourceManifest manifest,
        LogSpaceResourceOptions? options = null)
        : base(manifest, options ?? new LogSpaceResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => LogSpacePlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return LogSpacePlanner.CreatePlan(context);
    }
}
