using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class SecretStorePlanner
{
    private const string SecretStoreKind = "SecretStore";
    private const string ApiEndpointName = "api";
    private const string DataMountName = "data";
    private const string ControlPlanePath = "/cohesion/v1";

    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, SecretStoreKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SecretStore planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires a StatefulSet workload for stable replica identity.");
        }

        RequireManifestShape(manifest);

        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        RequirePlanShape(plan, manifest);
        return plan;
    }

    private static void RequireManifestShape(ResourceManifest manifest)
    {
        ResourceManifestEndpoint? apiEndpoint = null;
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            if (string.Equals(endpoint.Name, ApiEndpointName, StringComparison.Ordinal))
            {
                apiEndpoint = endpoint;
                break;
            }
        }

        if (apiEndpoint is null)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires an endpoint named '{ApiEndpointName}'.");
        }

        if ((!string.Equals(apiEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(apiEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(apiEndpoint.Protocol, "tcp", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SecretStore endpoint '{ApiEndpointName}' must use HTTP or HTTPS over TCP.");
        }

        int dataVolumeCount = 0;
        int persistentVolumeCount = 0;
        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = manifest.Mounts[index];
            if (mount.Kind is not ResourceMountKind.Volume)
            {
                continue;
            }

            persistentVolumeCount++;
            if (string.Equals(mount.Name, DataMountName, StringComparison.Ordinal))
            {
                dataVolumeCount++;
            }
        }

        if (persistentVolumeCount is not 1 || dataVolumeCount is not 1)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires exactly one persistent Volume mount named '{DataMountName}'.");
        }

        if (!string.Equals(manifest.ControlPlane.Endpoint, ApiEndpointName, StringComparison.Ordinal) ||
            !string.Equals(manifest.ControlPlane.Path, ControlPlanePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires its '{ApiEndpointName}' control plane at '{ControlPlanePath}'.");
        }
    }

    private static void RequirePlanShape(ResourcePlan plan, ResourceManifest manifest)
    {
        if (plan.Workload.Kind is not WorkloadKind.StatefulSet || !plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires stable StatefulSet workload identity.");
        }

        if (plan.Workload.Replicas is not 1)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires exactly one replica until a replication protocol is configured.");
        }

        if (plan.Volumes.Count is not 1)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' must produce exactly one persistent volume.");
        }

        VolumeSpec volume = plan.Volumes[0];
        if (!string.Equals(volume.Name, DataMountName, StringComparison.Ordinal) ||
            volume.Kind is not ResourceMountKind.Volume ||
            !volume.PerReplicaClaim ||
            string.IsNullOrWhiteSpace(volume.Size))
        {
            throw new InvalidOperationException(
                $"SecretStore volume '{DataMountName}' must be a sized per-replica claim.");
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
            }

            if (service.Endpoint is null &&
                service.Port is null &&
                service.Headless &&
                service.Governing)
            {
                governingServiceCount++;
            }
        }

        if (apiServiceCount is not 1 || governingServiceCount is not 1)
        {
            throw new InvalidOperationException(
                $"SecretStore resource '{manifest.Name}' requires one API service and one headless governing service.");
        }
    }
}
