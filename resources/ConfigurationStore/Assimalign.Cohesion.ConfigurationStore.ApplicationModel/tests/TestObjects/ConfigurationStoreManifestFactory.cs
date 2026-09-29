using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

internal static class ConfigurationStoreManifestFactory
{
    internal static ResourceManifest Create(
        string name = "appa-configuration",
        string application = "appa")
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "ConfigurationStore",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.ConfigurationStore.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.ConfigurationStore",
                Composable = true,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "api",
                    Scheme = "https",
                    Protocol = "tcp",
                    ContainerPort = 8443,
                },
            ],
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe
                {
                    Endpoint = "api",
                    Http = "/readyz",
                },
                Liveness = new ResourceManifestProbe
                {
                    Endpoint = "api",
                    Http = "/livez",
                },
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "api",
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
