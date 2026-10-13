using System;
using System.Globalization;
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
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.UnsupportedResourceKind, manifest.Name, manifest.Kind));
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.Deployment)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.StatelessDeploymentRequired, manifest.Name));
        }

        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            if (manifest.Mounts[index].Kind is ResourceMountKind.Volume)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.VolumeMountNotSupported, manifest.Name));
            }
        }

        WebResourceOptions options = context.GetOptions<WebResourceOptions>();
        if (options.Storage.Size is not null)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.StorageSizeOverrideNotSupported, manifest.Name));
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
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlannedWorkloadMustBeStateless, manifest.Name));
        }

        if (plan.Volumes.Count is not 0 || plan.Services.Count != manifest.Endpoints.Count)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.InvalidPlannedVolumesOrServices, manifest.Name));
        }

        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (service.Endpoint is null || service.Headless || service.Governing)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlannedServiceHasNoEndpoint, manifest.Name, service.Name));
            }
        }
    }
}
