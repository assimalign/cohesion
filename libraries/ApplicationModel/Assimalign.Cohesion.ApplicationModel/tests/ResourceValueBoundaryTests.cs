using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ResourceValueBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject invalid resource HTTP probe paths at composition")]
    [InlineData("ready")]
    [InlineData("/ready?full=true")]
    [InlineData("/ready#state")]
    public void Validate_InvalidHttpProbePath_ShouldRejectBeforeReconcile(string path)
    {
        // Arrange
        ResourceManifest manifest = CreateManifest();

        // Act and Assert
        Should.Throw<ArgumentException>(() => (manifest with
        {
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe { Endpoint = "http", Http = path }
            }
        }).Validate());
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject invalid control-plane paths at composition")]
    [InlineData("cohesion/v1")]
    [InlineData("/cohesion?version=1")]
    [InlineData("/cohesion#version")]
    public void Validate_InvalidControlPlanePath_ShouldRejectBeforeReconcile(string path)
    {
        // Arrange
        ResourceManifest manifest = CreateManifest();

        // Act and Assert
        Should.Throw<ArgumentException>(() => (manifest with
        {
            ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = path }
        }).Validate());
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject blank mount-source names and keys at composition")]
    [InlineData("parameter:   ")]
    [InlineData("store:   ")]
    [InlineData("   :key")]
    public void Validate_BlankMountSourceComponent_ShouldRejectBeforeResolution(string source)
    {
        // Arrange
        ResourceManifest manifest = CreateManifest();

        // Act and Assert
        Should.Throw<ArgumentException>(() => (manifest with
        {
            Mounts = [new ResourceManifestMount
            {
                Name = "config",
                Kind = ResourceMountKind.Configuration,
                ContainerPath = "/config",
                Source = source
            }]
        }).Validate());
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Present default resource values cannot bypass validation")]
    [InlineData("http")]
    [InlineData("path")]
    [InlineData("source")]
    public void Validate_PresentDefaultValue_ShouldReject(string member)
    {
        // Arrange
        ResourceManifest original = CreateManifest();
        ResourceManifest manifest = member switch
        {
            "http" => original with
            {
                Probes = new ResourceManifestProbes
                {
                    Readiness = new ResourceManifestProbe { Endpoint = "http", Http = default(ResourceHttpPath) }
                }
            },
            "path" => original with
            {
                ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = default }
            },
            _ => original with
            {
                Mounts = [new ResourceManifestMount
                {
                    Name = "config", Kind = ResourceMountKind.Configuration,
                    ContainerPath = "/config", Source = default(ResourceMountSource)
                }]
            }
        };

        // Act and Assert
        Should.Throw<System.IO.InvalidDataException>(() => manifest.Validate());
    }

    private static ResourceManifest CreateManifest() => new()
    {
        Name = "app-api",
        Kind = "Web",
        Application = "app",
        ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = "Example.Api" },
        Endpoints = [new ResourceManifestEndpoint
        {
            Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080
        }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment },
        Properties = new Dictionary<string, string>()
    };
}
