using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class DatabasePlanner
{
    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, "Database", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure, manifest.Name, manifest.Kind));
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure2, manifest.Name));
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
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure3, manifest.Name));
        }

        ResourcePlan plan = GenericPlanner.CreatePlan(context);

        if (!plan.Workload.StableIdentity)
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure4, manifest.Name));
        }

        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            if (!volume.PerReplicaClaim || string.IsNullOrWhiteSpace(volume.Size))
            {
                throw new InvalidOperationException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure5, volume.Name));
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
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure6, manifest.Name, service.Name));
            }

            if (service.Headless && service.Governing)
            {
                governingServiceCount++;
            }
        }

        if (governingServiceCount is not 1)
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabasePlannerCreatePlanFailure7, manifest.Name));
        }

        return plan;
    }
}
