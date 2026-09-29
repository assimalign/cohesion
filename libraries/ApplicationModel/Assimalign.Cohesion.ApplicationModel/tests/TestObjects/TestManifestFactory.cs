using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

internal static class TestManifestFactory
{
    public static ResourceManifest Create(
        string name = "worker",
        string application = "appa",
        int replicas = 1,
        int? maxReplicas = 3)
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "Worker",
            Application = application,
            ApplicationModel = "Example.Worker.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.Worker",
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "control",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
            ],
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "control",
                Path = "/cohesion/v1",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = replicas,
                MaxReplicas = maxReplicas,
            },
        };
    }
}
