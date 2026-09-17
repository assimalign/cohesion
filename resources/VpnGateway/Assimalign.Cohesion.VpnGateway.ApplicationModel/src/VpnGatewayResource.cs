using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.VpnGateway.ApplicationModel;

/// <summary>Represents a manifest-backed VpnGateway workload in an application graph.</summary>
public sealed class VpnGatewayResource : PlannedResource
{
    private const string VpnGatewayPlannerName = "VpnGateway planner";

    /// <summary>Initializes a VpnGateway resource.</summary>
    /// <param name="manifest">The VpnGateway resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public VpnGatewayResource(
        ResourceManifest manifest,
        VpnGatewayResourceOptions? options = null)
        : base(manifest, options ?? new VpnGatewayResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => VpnGatewayPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return VpnGatewayPlanner.CreatePlan(context);
    }
}
