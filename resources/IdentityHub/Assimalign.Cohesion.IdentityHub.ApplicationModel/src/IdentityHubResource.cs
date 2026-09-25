using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion identity hub in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled identity-hub executable, and its realization plan contains
/// no platform-specific objects or IdentityHub runtime dependencies.
/// </remarks>
public sealed class IdentityHubResource : PlannedResource
{
    private const string IdentityHubPlannerName = "IdentityHub planner";

    /// <summary>
    /// Initializes a new <see cref="IdentityHubResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The identity-hub resource manifest.</param>
    /// <param name="options">Optional deployer-owned storage overrides.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public IdentityHubResource(
        ResourceManifest manifest,
        IdentityHubResourceOptions? options = null)
        : base(manifest, options ?? new IdentityHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => IdentityHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return IdentityHubPlanner.CreatePlan(context);
    }
}
