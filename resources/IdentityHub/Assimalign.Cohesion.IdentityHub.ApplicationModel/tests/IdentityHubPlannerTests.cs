using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

public sealed class IdentityHubPlannerTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - CreatePlan: matches the IdentityHub golden fixture")]
    public void CreatePlan_WithIdentityHubManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        var resource = new IdentityHubResource(IdentityHubManifestFactory.Create());
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "identity-hub.json");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - CreatePlan: applies storage override to singleton stable storage")]
    public void CreatePlan_WithStorageOverride_ShouldApplyStableSingletonStorageSemantics()
    {
        // Arrange
        var options = new IdentityHubResourceOptions();
        options.Storage.Size = "30Gi";
        var resource = new IdentityHubResource(IdentityHubManifestFactory.Create(), options);

        // Act
        ResourcePlan plan = resource.CreatePlan(CreateContext(resource));

        // Assert
        plan.Workload.Kind.ShouldBe(WorkloadKind.StatefulSet);
        plan.Workload.Replicas.ShouldBe(1);
        plan.Workload.StableIdentity.ShouldBeTrue();
        VolumeSpec volume = plan.Volumes.ShouldHaveSingleItem();
        volume.Name.ShouldBe("data");
        volume.Size.ShouldBe("30Gi");
        volume.PerReplicaClaim.ShouldBeTrue();
        plan.Services.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - CreatePlan: preserves additional endpoints and non-persistent mounts")]
    public void CreatePlan_WithAdditionalManifestTraits_ShouldPreserveGenericPlanMappings()
    {
        // Arrange
        ResourceManifest source = IdentityHubManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                .. source.Endpoints,
                new ResourceManifestEndpoint
                {
                    Name = "metrics",
                    Scheme = "http",
                    Protocol = "tcp",
                    ContainerPort = 9090,
                },
            ],
            Mounts =
            [
                .. source.Mounts,
                new ResourceManifestMount
                {
                    Name = "tls",
                    Kind = ResourceMountKind.Secret,
                    ContainerPath = "/cohesion/mounts/tls",
                    Source = "parameter:identity-hub-tls",
                },
            ],
        };
        var resource = new IdentityHubResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        plan.Container.Ports.Count.ShouldBe(2);
        plan.Container.Mounts.Count.ShouldBe(2);
        plan.Volumes.ShouldHaveSingleItem().Name.ShouldBe("data");
        plan.Services.Count.ShouldBe(3);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - CreatePlan: rejects replica scaling without a replication protocol")]
    public void CreatePlan_WithReplicaOverride_ShouldRejectUnsafeScaling()
    {
        // Arrange
        ResourceManifest source = IdentityHubManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { MaxReplicas = 3 },
        };
        var resource = new IdentityHubResource(
            manifest,
            new IdentityHubResourceOptions { Replicas = 2 });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one replica", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - CreatePlan: rejects a non-HTTPS endpoint")]
    public void CreatePlan_WithNonHttpsEndpoint_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = IdentityHubManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints = [source.Endpoints[0] with { Scheme = "http" }],
        };
        var resource = new IdentityHubResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("HTTPS endpoint", Case.Sensitive);
    }

    private static PlanContext CreateContext(IdentityHubResource resource)
    {
        return new PlanContext(
            resource.Manifest,
            resource.Options,
            new TestApplicationEnvironment(),
            new Dictionary<string, ResourceManifest>());
    }

    private static void ShouldMatchFixture(ResourcePlan plan, string fixtureFileName)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "plans", fixtureFileName);
        string fixtureJson = File.ReadAllText(fixturePath);
        string actualJson = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);

        using JsonDocument fixtureDocument = JsonDocument.Parse(fixtureJson);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);

        JsonElement.DeepEquals(actualDocument.RootElement, fixtureDocument.RootElement)
            .ShouldBeTrue($"Plan output did not match golden fixture '{fixtureFileName}'.");
    }

    private sealed class TestApplicationEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Development";

        public bool IsDevelopment => true;
    }
}
