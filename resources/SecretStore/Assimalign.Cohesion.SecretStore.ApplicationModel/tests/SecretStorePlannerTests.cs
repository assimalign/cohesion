using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

public sealed class SecretStorePlannerTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: matches the SecretStore golden fixture")]
    public void CreatePlan_WithSecretStoreManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = SecretStoreManifestFactory.Create();
        var resource = new SecretStoreResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "secret-store.json");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: applies a storage override to stable single-replica storage")]
    public void CreatePlan_WithStorageOverride_ShouldApplyStableSingleReplicaStorageSemantics()
    {
        // Arrange
        ResourceManifest manifest = SecretStoreManifestFactory.Create();
        var options = new SecretStoreResourceOptions();
        options.Storage.Size = "30Gi";
        var resource = new SecretStoreResource(manifest, options);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        plan.Workload.Kind.ShouldBe(WorkloadKind.StatefulSet);
        plan.Workload.Replicas.ShouldBe(1);
        plan.Workload.StableIdentity.ShouldBeTrue();

        VolumeSpec volume = plan.Volumes.ShouldHaveSingleItem();
        volume.Name.ShouldBe("data");
        volume.Size.ShouldBe("30Gi");
        volume.PerReplicaClaim.ShouldBeTrue();

        ServiceSpec apiService = plan.Services
            .Where(service => service.Endpoint == "api")
            .ShouldHaveSingleItem();
        apiService.Headless.ShouldBeFalse();
        apiService.Governing.ShouldBeFalse();

        ServiceSpec governingService = plan.Services
            .Where(service => service.Headless && service.Governing)
            .ShouldHaveSingleItem();
        governingService.Endpoint.ShouldBeNull();
        governingService.Port.ShouldBeNull();
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects manifest replica scaling without a replication protocol")]
    public void CreatePlan_WithMultipleManifestReplicas_ShouldRejectUnsafeScaling()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with
            {
                Replicas = 2,
                MaxReplicas = 2,
            },
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("requires exactly one replica", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects a replica override without a replication protocol")]
    public void CreatePlan_WithMultipleReplicaOverride_ShouldRejectUnsafeScaling()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { MaxReplicas = 2 },
        };
        var resource = new SecretStoreResource(
            manifest,
            new SecretStoreResourceOptions { Replicas = 2 });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("requires exactly one replica", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: preserves additional endpoints and non-persistent mounts")]
    public void CreatePlan_WithAdditionalManifestTraits_ShouldPreserveGenericPlanMappings()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
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
                    Source = "parameter:secretstore-tls",
                },
            ],
        };
        var resource = new SecretStoreResource(manifest);
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

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects a non-SecretStore kind")]
    public void CreatePlan_WithWrongKind_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with { Kind = "Database" };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("cannot plan", Case.Sensitive);
        error.Message.ShouldContain("Database", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects a stateless workload")]
    public void CreatePlan_WithDeploymentWorkload_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { Workload = WorkloadKind.Deployment },
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("StatefulSet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: requires the API endpoint")]
    public void CreatePlan_WithoutApiEndpoint_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                source.Endpoints[0] with { Name = "admin" },
            ],
            ControlPlane = source.ControlPlane with { Endpoint = "admin" },
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("endpoint named 'api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: requires an HTTP API over TCP")]
    public void CreatePlan_WithNonHttpApiEndpoint_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                source.Endpoints[0] with { Scheme = "secretstore" },
            ],
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("HTTP or HTTPS over TCP", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: requires exactly the persistent data Volume")]
    public void CreatePlan_WithWrongPersistentMount_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts =
            [
                source.Mounts[0] with { Name = "state" },
            ],
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("persistent Volume mount named 'data'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects a second persistent Volume")]
    public void CreatePlan_WithAdditionalPersistentVolume_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts =
            [
                .. source.Mounts,
                new ResourceManifestMount
                {
                    Name = "journal",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/journal",
                    Size = "5Gi",
                },
            ],
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one persistent Volume", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: requires the default control-plane location")]
    public void CreatePlan_WithWrongControlPlanePath_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = SecretStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            ControlPlane = source.ControlPlane with { Path = "/admin" },
        };
        var resource = new SecretStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("'/cohesion/v1'", Case.Sensitive);
    }

    private static PlanContext CreateContext(SecretStoreResource resource)
    {
        return new PlanContext(
            resource.Manifest,
            resource.Options,
            new TestApplicationEnvironment(),
            new Dictionary<string, ResourceManifest>());
    }

    private static void ShouldMatchFixture(ResourcePlan plan, string fixtureFileName)
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "plans",
            fixtureFileName);
        string fixtureJson = File.ReadAllText(fixturePath);
        string actualJson = JsonSerializer.Serialize(
            plan,
            ResourcePlanJsonContext.Default.ResourcePlan);

        _ = JsonSerializer.Deserialize(
            fixtureJson,
            ResourcePlanJsonContext.Default.ResourcePlan)
            ?? throw new InvalidOperationException(
                $"Golden plan fixture '{fixtureFileName}' deserialized to null.");

        using JsonDocument fixtureDocument = JsonDocument.Parse(fixtureJson);
        using JsonDocument actualDocument = JsonDocument.Parse(actualJson);

        JsonElement.DeepEquals(
            actualDocument.RootElement,
            fixtureDocument.RootElement)
            .ShouldBeTrue($"Plan output did not match golden fixture '{fixtureFileName}'.");
    }

    private sealed class TestApplicationEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Development";

        public bool IsDevelopment => true;
    }
}
