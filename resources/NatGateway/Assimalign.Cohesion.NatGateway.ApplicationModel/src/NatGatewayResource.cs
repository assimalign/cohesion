using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed NatGateway workload in an application graph.</summary>
public sealed class NatGatewayResource : PlannedResource
{
    private const string NatGatewayPlannerName = "NatGateway planner";

    /// <summary>Initializes a NatGateway resource.</summary>
    /// <param name="manifest">The NatGateway resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public NatGatewayResource(
        ResourceManifest manifest,
        NatGatewayResourceOptions? options = null)
        : base(manifest, options ?? new NatGatewayResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => NatGatewayPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return NatGatewayPlanner.CreatePlan(context);
    }
}
