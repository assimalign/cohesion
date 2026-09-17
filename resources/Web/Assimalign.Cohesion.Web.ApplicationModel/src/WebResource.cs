using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Web.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion Web workload in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Platform gateways
/// compile its <see cref="ResourcePlan"/> without loading this assembly.
/// </remarks>
public sealed class WebResource : PlannedResource
{
    private const string WebPlannerName = "Web planner";

    /// <summary>
    /// Initializes a new <see cref="WebResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The Web resource manifest.</param>
    /// <param name="options">Optional replica overrides.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public WebResource(
        ResourceManifest manifest,
        WebResourceOptions? options = null)
        : base(manifest, options ?? new WebResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => WebPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return WebPlanner.CreatePlan(context);
    }
}
