using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Minimal manifests of application <c>appa</c>: a store resource of a chosen kind and a consumer
/// whose Configuration mount reads <c>&lt;store&gt;:&lt;namespace&gt;</c>.
/// </summary>
internal static class TestManifests
{
    internal static ResourceManifest Store(string name = SourceRequestFactory.StoreName, string kind = "ConfigurationStore") =>
        Create(name, kind);

    internal static ResourceManifest Consumer(string name, string source) =>
        Create(name, "Worker") with
        {
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "settings",
                    Kind = ResourceMountKind.Configuration,
                    ContainerPath = "/cohesion/mounts/settings",
                    Source = source,
                },
            ],
        };

    private static ResourceManifest Create(string name, string kind) => new()
    {
        Name = name,
        Kind = kind,
        Application = "appa",
        ApplicationModel = "Example.AppA.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Example.AppA",
        },
        Endpoints =
        [
            new ResourceManifestEndpoint
            {
                Name = "api",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        ],
        ControlPlane = new ResourceManifestControlPlane
        {
            Endpoint = "api",
            Path = "/cohesion/v1",
        },
        Lifecycle = new ResourceManifestLifecycle
        {
            Workload = WorkloadKind.Deployment,
            Replicas = 1,
            MaxReplicas = 1,
        },
    };
}
