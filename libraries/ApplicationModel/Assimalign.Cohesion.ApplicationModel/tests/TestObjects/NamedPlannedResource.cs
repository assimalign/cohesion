using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal sealed class NamedPlannedResource : PlannedResource
{
    public NamedPlannedResource(ResourceManifest manifest, string plannerName)
        : base(manifest)
    {
        PlannerName = plannerName;
    }

    public override string PlannerName { get; }
}
