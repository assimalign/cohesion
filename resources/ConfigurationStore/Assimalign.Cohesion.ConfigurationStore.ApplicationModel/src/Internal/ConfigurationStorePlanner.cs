using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel;

internal static class ConfigurationStorePlanner
{
    private const string ConfigurationStoreKind = "ConfigurationStore";
    private const string ApiEndpointName = "api";
    private const string DataMountName = "data";

    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, ConfigurationStoreKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConfigurationStore planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires a StatefulSet workload for stable replica identity.");
        }

        RequireManifestShape(manifest);

        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        RequirePlanShape(plan, manifest);
        return plan;
    }

    private static void RequireManifestShape(ResourceManifest manifest)
    {
        if (manifest.Endpoints.Count is not 1 ||
            !string.Equals(manifest.Endpoints[0].Name, ApiEndpointName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires exactly one endpoint named '{ApiEndpointName}'.");
        }

        if (manifest.Mounts.Count is not 1 ||
            !string.Equals(manifest.Mounts[0].Name, DataMountName, StringComparison.Ordinal) ||
            manifest.Mounts[0].Kind is not ResourceMountKind.Volume)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires exactly one Volume mount named '{DataMountName}'.");
        }
    }

    private static void RequirePlanShape(ResourcePlan plan, ResourceManifest manifest)
    {
        if (plan.Workload.Kind is not WorkloadKind.StatefulSet || !plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires stable StatefulSet workload identity.");
        }

        if (plan.Workload.Replicas is not 1)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires exactly one replica until a replication protocol is configured.");
        }

        if (plan.Volumes.Count is not 1)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' must produce exactly one persistent volume.");
        }

        VolumeSpec volume = plan.Volumes[0];
        if (!string.Equals(volume.Name, DataMountName, StringComparison.Ordinal) ||
            volume.Kind is not ResourceMountKind.Volume ||
            !volume.PerReplicaClaim ||
            string.IsNullOrWhiteSpace(volume.Size))
        {
            throw new InvalidOperationException(
                $"ConfigurationStore volume '{DataMountName}' must be a sized per-replica claim.");
        }

        int apiServiceCount = 0;
        int governingServiceCount = 0;

        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (string.Equals(service.Endpoint, ApiEndpointName, StringComparison.Ordinal) &&
                !service.Headless &&
                !service.Governing)
            {
                apiServiceCount++;
                continue;
            }

            if (service.Endpoint is null &&
                service.Port is null &&
                service.Headless &&
                service.Governing)
            {
                governingServiceCount++;
                continue;
            }

            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' produced unexpected service '{service.Name}'.");
        }

        if (plan.Services.Count is not 2 || apiServiceCount is not 1 || governingServiceCount is not 1)
        {
            throw new InvalidOperationException(
                $"ConfigurationStore resource '{manifest.Name}' requires one API service and one headless governing service.");
        }
    }
}
