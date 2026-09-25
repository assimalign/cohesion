using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed Rezolvr workload in an application graph.</summary>
public sealed class RezolvrResource : PlannedResource
{
    private const string RezolvrPlannerName = "Rezolvr planner";

    /// <summary>Initializes a Rezolvr resource.</summary>
    /// <param name="manifest">The Rezolvr resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public RezolvrResource(
        ResourceManifest manifest,
        RezolvrResourceOptions? options = null)
        : base(manifest, options ?? new RezolvrResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => RezolvrPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return RezolvrPlanner.CreatePlan(context);
    }
}
