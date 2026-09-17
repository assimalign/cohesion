using System;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.NotificationHub.ApplicationModel;

internal static class NotificationHubPlanner
{
    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, "NotificationHub", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"NotificationHub planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.Deployment)
        {
            throw new InvalidOperationException("NotificationHub requires a Deployment workload.");
        }
        if (manifest.ControlPlane.Endpoint != "http" || manifest.ControlPlane.Path != "/cohesion/v1")
        {
            throw new InvalidOperationException("NotificationHub requires the 'http' endpoint and '/cohesion/v1' control-plane path.");
        }
        RequireEndpoint(manifest, "http", "http", "tcp");

        return GenericPlanner.CreatePlan(context);
    }

    private static void RequireEndpoint(ResourceManifest manifest, string name, string scheme, string protocol)
    {
        if (!manifest.Endpoints.Any(endpoint => endpoint.Name == name &&
            endpoint.Scheme == scheme && endpoint.Protocol == protocol))
        {
            throw new InvalidOperationException($"NotificationHub requires the '{name}' {scheme}/{protocol} endpoint.");
        }
    }
}
