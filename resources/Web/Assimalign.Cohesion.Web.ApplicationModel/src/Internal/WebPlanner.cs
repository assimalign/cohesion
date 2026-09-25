using System;
using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class WebPlanner
{
    private const string WebKind = "Web";

    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, WebKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Web planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.Deployment)
        {
            throw new InvalidOperationException(
                $"Web resource '{manifest.Name}' requires a stateless Deployment workload.");
        }

        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            if (manifest.Mounts[index].Kind is ResourceMountKind.Volume)
            {
                throw new InvalidOperationException(
                    $"Web resource '{manifest.Name}' is stateless and cannot declare a Volume mount.");
            }
        }

        WebResourceOptions options = context.GetOptions<WebResourceOptions>();
        if (options.Storage.Size is not null)
        {
            throw new InvalidOperationException(
                $"Web resource '{manifest.Name}' is stateless and cannot configure a storage-size override.");
        }

        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        RequireWebShape(plan, manifest);
        return plan;
    }

    private static void RequireWebShape(ResourcePlan plan, ResourceManifest manifest)
    {
        if (plan.Workload.Kind is not WorkloadKind.Deployment || plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                $"Web resource '{manifest.Name}' did not produce a stateless Deployment workload.");
        }

        if (plan.Volumes.Count is not 0 || plan.Services.Count != manifest.Endpoints.Count)
        {
            throw new InvalidOperationException(
                $"Web resource '{manifest.Name}' must produce no volumes and exactly one service per endpoint.");
        }

        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (service.Endpoint is null || service.Headless || service.Governing)
            {
                throw new InvalidOperationException(
                    $"Web resource '{manifest.Name}' produced a non-endpoint service '{service.Name}'.");
            }
        }
    }
}
