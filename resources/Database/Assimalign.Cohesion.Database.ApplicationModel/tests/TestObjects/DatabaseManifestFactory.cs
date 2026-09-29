using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

internal static class DatabaseManifestFactory
{
    internal static ResourceManifest Create(
        string name = "appa-database",
        string application = "appa")
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "Database",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.Database.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.Database",
                Composable = true,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "db",
                    Scheme = "cohesion-db",
                    Protocol = "tcp",
                    ContainerPort = 5740,
                },
                new ResourceManifestEndpoint
                {
                    Name = "admin",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8081,
                },
            ],
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe
                {
                    Endpoint = "admin",
                    Http = "/readyz",
                },
                Liveness = new ResourceManifestProbe
                {
                    Endpoint = "admin",
                    Http = "/livez",
                },
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "admin",
                Path = "/cohesion/v1",
            },
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "data",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/data",
                    Size = "10Gi",
                },
            ],
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                Replicas = 1,
                MaxReplicas = 1,
                StopGraceSeconds = 30,
            },
        };
    }
}
