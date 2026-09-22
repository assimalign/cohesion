using System;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

internal static class RezolvrPlanner
{
    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, "Rezolvr", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rezolvr planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.Deployment)
        {
            throw new InvalidOperationException("Rezolvr requires a Deployment workload.");
        }
        if (manifest.ControlPlane.Endpoint != "admin" || manifest.ControlPlane.Path != "/cohesion/v1")
        {
            throw new InvalidOperationException("Rezolvr requires the 'admin' endpoint and '/cohesion/v1' control-plane path.");
        }
        RequireEndpoint(manifest, "dns", "dns", "udp");
        RequireEndpoint(manifest, "dns-tcp", "dns", "tcp");
        RequireEndpoint(manifest, "admin", "http", "tcp");

        return GenericPlanner.CreatePlan(context);
    }

    private static void RequireEndpoint(ResourceManifest manifest, string name, string scheme, string protocol)
    {
        if (!manifest.Endpoints.Any(endpoint => endpoint.Name == name &&
            endpoint.Scheme == scheme && endpoint.Protocol == protocol))
        {
            throw new InvalidOperationException($"Rezolvr requires the '{name}' {scheme}/{protocol} endpoint.");
        }
    }
}
