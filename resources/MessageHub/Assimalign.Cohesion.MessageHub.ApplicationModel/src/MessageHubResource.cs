using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.MessageHub.ApplicationModel;

/// <summary>Represents a manifest-backed MessageHub workload in an application graph.</summary>
public sealed class MessageHubResource : PlannedResource
{
    private const string MessageHubPlannerName = "MessageHub planner";

    /// <summary>Initializes a MessageHub resource.</summary>
    /// <param name="manifest">The MessageHub resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public MessageHubResource(
        ResourceManifest manifest,
        MessageHubResourceOptions? options = null)
        : base(manifest, options ?? new MessageHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => MessageHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return MessageHubPlanner.CreatePlan(context);
    }
}
