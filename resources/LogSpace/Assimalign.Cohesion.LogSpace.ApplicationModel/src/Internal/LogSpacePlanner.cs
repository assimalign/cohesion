using System;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.LogSpace.ApplicationModel;

internal static class LogSpacePlanner
{
    internal static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResourceManifest manifest = context.Manifest;
        if (!string.Equals(manifest.Kind, "LogSpace", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"LogSpace planner cannot plan resource '{manifest.Name}' with kind '{manifest.Kind}'.");
        }

        if (manifest.Lifecycle.Workload is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException("LogSpace requires a StatefulSet workload.");
        }
        if (manifest.ControlPlane.Endpoint != "query" || manifest.ControlPlane.Path != "/cohesion/v1")
        {
            throw new InvalidOperationException("LogSpace requires the 'query' endpoint and '/cohesion/v1' control-plane path.");
        }
        RequireEndpoint(manifest, "otlp", "https", "tcp");
        RequireEndpoint(manifest, "query", "https", "tcp");
        if (!manifest.Mounts.Any(static mount => mount.Name == "data" &&
            mount.Kind is ResourceMountKind.Volume && mount.ContainerPath == "/data" &&
            !string.IsNullOrWhiteSpace(mount.Size)))
        {
            throw new InvalidOperationException("LogSpace requires a sized 'data' Volume mount at '/data'.");
        }
        return GenericPlanner.CreatePlan(context);
    }

    private static void RequireEndpoint(ResourceManifest manifest, string name, string scheme, string protocol)
    {
        if (!manifest.Endpoints.Any(endpoint => endpoint.Name == name &&
            endpoint.Scheme == scheme && endpoint.Protocol == protocol))
        {
            throw new InvalidOperationException($"LogSpace requires the '{name}' {scheme}/{protocol} endpoint.");
        }
    }
}
