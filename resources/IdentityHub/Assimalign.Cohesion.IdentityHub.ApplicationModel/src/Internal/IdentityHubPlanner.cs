using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel;

internal static class IdentityHubPlanner
{
    private const string IdentityHubKind = "IdentityHub";
    private const string HttpsEndpointName = "https";
    private const string DataMountName = "data";
    private const string ControlPlanePath = "/cohesion/v1";

    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, IdentityHubKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IdentityHub planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires a StatefulSet workload for stable replica identity.");
        }

        RequireManifestShape(manifest);

        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        RequirePlanShape(plan, manifest);
        return plan;
    }

    private static void RequireManifestShape(ResourceManifest manifest)
    {
        ResourceManifestEndpoint? httpsEndpoint = null;
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            if (string.Equals(endpoint.Name, HttpsEndpointName, StringComparison.Ordinal))
            {
                httpsEndpoint = endpoint;
                break;
            }
        }

        if (httpsEndpoint is null ||
            !string.Equals(httpsEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(httpsEndpoint.Protocol, "tcp", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires an HTTPS endpoint named '{HttpsEndpointName}' over TCP.");
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
                $"IdentityHub resource '{manifest.Name}' requires exactly one persistent Volume mount named '{DataMountName}'.");
        }

        if (!string.Equals(manifest.ControlPlane.Endpoint, HttpsEndpointName, StringComparison.Ordinal) ||
            !string.Equals(manifest.ControlPlane.Path, ControlPlanePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires its '{HttpsEndpointName}' control plane at '{ControlPlanePath}'.");
        }
    }

    private static void RequirePlanShape(ResourcePlan plan, ResourceManifest manifest)
    {
        if (plan.Workload.Kind is not WorkloadKind.StatefulSet || !plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires stable StatefulSet workload identity.");
        }

        if (plan.Workload.Replicas is not 1)
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires exactly one replica until a replication protocol is configured.");
        }

        if (plan.Volumes.Count is not 1)
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' must produce exactly one persistent volume.");
        }

        VolumeSpec volume = plan.Volumes[0];
        if (!string.Equals(volume.Name, DataMountName, StringComparison.Ordinal) ||
            volume.Kind is not ResourceMountKind.Volume ||
            !volume.PerReplicaClaim ||
            string.IsNullOrWhiteSpace(volume.Size))
        {
            throw new InvalidOperationException(
                $"IdentityHub volume '{DataMountName}' must be a sized per-replica claim.");
        }

        int httpsServiceCount = 0;
        int governingServiceCount = 0;

        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (string.Equals(service.Endpoint, HttpsEndpointName, StringComparison.Ordinal) &&
                !service.Headless &&
                !service.Governing)
            {
                httpsServiceCount++;
            }

            if (service.Endpoint is null &&
                service.Port is null &&
                service.Headless &&
                service.Governing)
            {
                governingServiceCount++;
            }
        }

        if (httpsServiceCount is not 1 || governingServiceCount is not 1)
        {
            throw new InvalidOperationException(
                $"IdentityHub resource '{manifest.Name}' requires one HTTPS service and one headless governing service.");
        }
    }
}
