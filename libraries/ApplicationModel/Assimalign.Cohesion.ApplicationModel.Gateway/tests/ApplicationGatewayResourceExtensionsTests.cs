using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class ApplicationGatewayResourceExtensionsTests
{
    private const string Image =
        "docker.io/library/redis@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - AddContainer: Creates a digest-pinned generic plan")]
    public void AddContainer_DigestPinnedImage_ShouldCreateGenericPlan()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = Path.Combine(Path.GetTempPath(), "cohesion-container-tests", Guid.NewGuid().ToString("N")),
        };
        var gateway = new TestGateway(
            new InMemoryResourceStateManager(),
            [new RecordingController(reconciled, deleted)],
            options: options);
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(gateway);

        // Act
        IApplicationResourceDescriptor descriptor = builder.AddContainer(
            "redis",
            Image,
            container => container
                .AddEndpoint(new ResourceEndpoint("tcp", "tcp", 6379))
                .UseReadinessProbe(ProbeSpec.Tcp("tcp"))
                .AddEnvironment("REDIS_MODE", "standalone"));
        IApplicationModel model = builder.Build().Model;

        // Assert
        descriptor.Resource.Name.ShouldBe((ResourceName)"redis");
        model.Descriptors[0].Plan.ShouldNotBeNull().Container.Artifact.ShouldBe(ArtifactRef.Self);
        ResourceManifest manifest = model.Manifests[0];
        manifest.Kind.ShouldBe("Container");
        manifest.Artifact.Image.ShouldBe(Image);
        manifest.Probes.Readiness.ShouldNotBeNull().Tcp.ShouldBe(true);
        manifest.EnvironmentVariables["REDIS_MODE"].ShouldBe("standalone");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - AddContainer: Rejects a mutable image tag")]
    public void AddContainer_MutableImageTag_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);

        // Act
        Action add = () => builder.AddContainer(
            "redis",
            "docker.io/library/redis:latest",
            container => container
                .AddEndpoint(new ResourceEndpoint("tcp", "tcp", 6379))
                .UseReadinessProbe(ProbeSpec.Tcp("tcp")));

        // Assert
        Should.Throw<ArgumentException>(add).Message.ShouldContain("sha256");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - AddExecutable: Rejects a probe that names an undeclared endpoint")]
    public void AddExecutable_UnknownProbeEndpoint_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);

        // Act
        Action add = () => builder.AddExecutable(
            "worker",
            "worker.exe",
            executable => executable
                .AddEndpoint(new ResourceEndpoint("health", "http", 8080))
                .UseReadinessProbe(ProbeSpec.Tcp("missing")));

        // Assert
        Should.Throw<InvalidOperationException>(add).Message.ShouldContain("missing");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local validation: Rejects image-only resources before gather")]
    public void Validate_LocalGatewayWithContainer_ShouldRejectBeforeGather()
    {
        // Arrange
        string stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "cohesion-container-tests",
            Guid.NewGuid().ToString("N"));
        var gateway = new LocalGateway(new LocalGatewayOptions { StateDirectory = stateDirectory });
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), ["--environment", "Development"])
            .UseGateway(gateway);
        builder.AddContainer(
            "redis",
            Image,
            container => container
                .AddEndpoint(new ResourceEndpoint("tcp", "tcp", 6379))
                .UseReadinessProbe(ProbeSpec.Tcp("tcp")));
        // Act
        Action validate = () => builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(validate).Message.ShouldContain("image-only");
    }
}
