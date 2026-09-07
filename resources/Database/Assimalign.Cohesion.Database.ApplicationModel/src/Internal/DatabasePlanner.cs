using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

internal static class DatabasePlanner
{
    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, "Database", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                $"Database resource '{manifest.Name}' requires a StatefulSet workload for stable replica identity.");
        }

        bool hasVolume = false;
        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            if (manifest.Mounts[index].Kind is ResourceMountKind.Volume)
            {
                hasVolume = true;
                break;
            }
        }

        if (!hasVolume)
        {
            throw new InvalidOperationException(
                $"Database resource '{manifest.Name}' requires at least one persistent Volume mount.");
        }

        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        if (!plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                $"Database resource '{manifest.Name}' requires stable workload identity.");
        }

        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            if (!volume.PerReplicaClaim || string.IsNullOrWhiteSpace(volume.Size))
            {
                throw new InvalidOperationException(
                    $"Database volume '{volume.Name}' must be a sized per-replica claim.");
            }
        }

        int governingServiceCount = 0;
        var serviceNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (!serviceNames.Add(service.Name))
            {
                throw new InvalidOperationException(
                    $"Database resource '{manifest.Name}' produces duplicate service name '{service.Name}'. " +
                    "Endpoint name 'headless' is reserved for the governing service.");
            }

            if (service.Headless && service.Governing)
            {
                governingServiceCount++;
            }
        }

        if (governingServiceCount is not 1)
        {
            throw new InvalidOperationException(
                $"Database resource '{manifest.Name}' requires exactly one headless governing service.");
        }

        return plan;
    }
}
