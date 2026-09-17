using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal sealed class CountingPlannedResource : PlannedResource
{
    public CountingPlannedResource(ResourceManifest manifest)
        : base(manifest)
    {
    }

    public int PlanCount { get; private set; }

    public IReadOnlyDictionary<string, ResourceManifest>? LastReferences { get; private set; }

    public override ResourcePlan CreatePlan(PlanContext context)
    {
        PlanCount++;
        LastReferences = context.References;
        return base.CreatePlan(context);
    }
}
