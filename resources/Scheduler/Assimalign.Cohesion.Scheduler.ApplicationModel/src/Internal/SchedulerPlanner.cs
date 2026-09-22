using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

internal static class SchedulerPlanner
{
    private const string SchedulerKind = "Scheduler";

    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, SchedulerKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Scheduler planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.Deployment ||
            manifest.Lifecycle.Replicas is not 1 ||
            manifest.Lifecycle.MaxReplicas is not 1)
        {
            throw new InvalidOperationException(
                $"Scheduler resource '{manifest.Name}' requires a singleton stateless Deployment workload.");
        }

        ResourceManifestEndpoint? httpEndpoint = null;
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            if (string.Equals(manifest.Endpoints[index].Name, "http", StringComparison.Ordinal))
            {
                httpEndpoint = manifest.Endpoints[index];
                break;
            }
        }

        if (httpEndpoint is null ||
            !string.Equals(httpEndpoint.Scheme, "http", StringComparison.Ordinal) ||
            !string.Equals(httpEndpoint.Protocol, "tcp", StringComparison.Ordinal) ||
            !string.Equals(manifest.ControlPlane.Endpoint, "http", StringComparison.Ordinal) ||
            !string.Equals(manifest.ControlPlane.Path, "/cohesion/v1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Scheduler resource '{manifest.Name}' requires an 'http' TCP endpoint and '/cohesion/v1' control-plane path.");
        }

        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            if (manifest.Mounts[index].Kind is ResourceMountKind.Volume)
            {
                throw new InvalidOperationException(
                    $"Scheduler resource '{manifest.Name}' is stateless and cannot declare a Volume mount.");
            }
        }

        SchedulerResourceOptions options = context.GetOptions<SchedulerResourceOptions>();
        if (options.Storage.Size is not null || options.Replicas is not null and not 1)
        {
            throw new InvalidOperationException(
                $"Scheduler resource '{manifest.Name}' must remain singleton and cannot configure storage.");
        }

        ResourcePlan plan = GenericPlanner.CreatePlan(context);
        if (plan.Workload.Kind is not WorkloadKind.Deployment ||
            plan.Workload.StableIdentity ||
            plan.Workload.Replicas is not 1 ||
            plan.Volumes.Count is not 0 ||
            plan.Services.Count != manifest.Endpoints.Count)
        {
            throw new InvalidOperationException(
                $"Scheduler resource '{manifest.Name}' did not produce the required stateless service shape.");
        }

        bool foundHttpService = false;
        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (service.Headless || service.Governing || service.Endpoint is null)
            {
                throw new InvalidOperationException(
                    $"Scheduler resource '{manifest.Name}' produced a non-endpoint service '{service.Name}'.");
            }

            foundHttpService |=
                string.Equals(service.Endpoint, "http", StringComparison.Ordinal) &&
                string.Equals(service.Protocol, "tcp", StringComparison.Ordinal);
        }

        if (!foundHttpService)
        {
            throw new InvalidOperationException(
                $"Scheduler resource '{manifest.Name}' did not produce its required http TCP service.");
        }

        return plan;
    }
}
