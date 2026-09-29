using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed EmailHub workload in an application graph.</summary>
public sealed class EmailHubResource : PlannedResource
{
    private const string EmailHubPlannerName = "EmailHub planner";

    /// <summary>Initializes a EmailHub resource.</summary>
    /// <param name="manifest">The EmailHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public EmailHubResource(
        ResourceManifest manifest,
        EmailHubResourceOptions? options = null)
        : base(manifest, options ?? new EmailHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => EmailHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return EmailHubPlanner.CreatePlan(context);
    }
}
