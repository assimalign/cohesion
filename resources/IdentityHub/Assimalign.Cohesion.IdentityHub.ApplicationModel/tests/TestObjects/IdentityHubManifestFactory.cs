using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

internal static class IdentityHubManifestFactory
{
    internal static ResourceManifest Create(
        string name = "appa-identity",
        string application = "appa")
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "IdentityHub",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.IdentityHub.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.IdentityHub",
                Composable = true,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "https",
                    Scheme = "https",
                    Protocol = "tcp",
                    ContainerPort = 8443,
                },
            ],
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe
                {
                    Endpoint = "https",
                    Http = "/readyz",
                },
                Liveness = new ResourceManifestProbe
                {
                    Endpoint = "https",
                    Http = "/livez",
                },
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "https",
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
