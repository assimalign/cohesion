using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

internal static class SecretStoreManifestFactory
{
    internal static ResourceManifest Create(
        string name = "appa-secretstore",
        string application = "appa")
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "SecretStore",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.SecretStore.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.SecretStore",
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
            Commands =
            [
                new ResourceManifestCommand("cohesion.trust.add"),
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
