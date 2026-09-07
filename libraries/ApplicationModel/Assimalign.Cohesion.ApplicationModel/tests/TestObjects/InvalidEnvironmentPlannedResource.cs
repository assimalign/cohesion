using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal sealed class InvalidEnvironmentPlannedResource : PlannedResource
{
    public InvalidEnvironmentPlannedResource(ResourceManifest manifest)
        : base(manifest)
    {
    }

    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ResourcePlan plan = base.CreatePlan(context);
        var environment = new Dictionary<string, string>(plan.Container.Environment, System.StringComparer.Ordinal)
        {
            [ResourceEnvironment.Application] = "foreign",
        };
        var container = new ContainerSpec(
            plan.Container.Name,
            plan.Container.Artifact,
            plan.Container.Ports,
            plan.Container.Mounts,
            environment,
            plan.Container.Probes);

        return new ResourcePlan(
            plan.Schema,
            plan.Resource,
            plan.Kind,
            plan.Workload,
            container,
            plan.Volumes,
            plan.Services,
            plan.Exposures,
            plan.Hints);
    }
}
