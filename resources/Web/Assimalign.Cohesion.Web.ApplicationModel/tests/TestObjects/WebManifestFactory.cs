using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Web.ApplicationModel.Tests;

internal static class WebManifestFactory
{
    internal static ResourceManifest Create(
        string name = "appa-api",
        string application = "appa")
    {
        return new ResourceManifest
        {
            Name = name,
            Kind = "Web",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.Api",
                Composable = true,
            },
            Endpoints =
            [
                new ResourceManifestEndpoint
                {
                    Name = "http",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 8080,
                },
                new ResourceManifestEndpoint
                {
                    Name = "https",
                    Scheme = "https",
                    Protocol = "tcp",
                    ContainerPort = 8443,
                    Public = true,
                    Certificate = "manifest-tls",
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
                    Endpoint = "http",
                    Tcp = true,
                },
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "http",
                Path = "/cohesion/v1",
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["CUSTOM_VALUE"] = "preserved",
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                Replicas = 2,
                MaxReplicas = 4,
                StopGraceSeconds = 30,
            },
            Properties = new Dictionary<string, string>
            {
                ["web.kind"] = "Api",
            },
        };
    }
}
