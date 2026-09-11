using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

public sealed class ConfigurationStorePlannerTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: matches the ConfigurationStore golden fixture")]
    public void CreatePlan_WithConfigurationStoreManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = ConfigurationStoreManifestFactory.Create();
        var resource = new ConfigurationStoreResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "configuration-store.json");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: applies storage overrides to stable singleton storage")]
    public void CreatePlan_WithStorageOverride_ShouldApplyStableSingletonStorageSemantics()
    {
        // Arrange
        ResourceManifest manifest = ConfigurationStoreManifestFactory.Create();
        var options = new ConfigurationStoreResourceOptions();
        options.Storage.Size = "30Gi";
        var resource = new ConfigurationStoreResource(manifest, options);
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

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: rejects replica scaling without a replication protocol")]
    public void CreatePlan_WithReplicaOverride_ShouldRejectUnsafeScaling()
    {
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { MaxReplicas = 3 },
        };
        var resource = new ConfigurationStoreResource(
            manifest,
            new ConfigurationStoreResourceOptions { Replicas = 3 });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        exception.Message.ShouldContain("requires exactly one replica");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: rejects a non-ConfigurationStore kind")]
    public void CreatePlan_WithWrongKind_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with { Kind = "Database" };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("cannot plan", Case.Sensitive);
        error.Message.ShouldContain("Database", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: rejects a stateless workload")]
    public void CreatePlan_WithDeploymentWorkload_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { Workload = WorkloadKind.Deployment },
        };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("StatefulSet", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: requires exactly the api endpoint")]
    public void CreatePlan_WithWrongEndpoint_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                source.Endpoints[0] with { Name = "admin" },
            ],
        };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one endpoint named 'api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: rejects an additional endpoint")]
    public void CreatePlan_WithAdditionalEndpoint_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
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
        };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one endpoint named 'api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: requires exactly the data Volume mount")]
    public void CreatePlan_WithWrongPersistentMount_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts =
            [
                source.Mounts[0] with { Name = "state" },
            ],
        };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one Volume mount named 'data'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - CreatePlan: rejects a non-Volume data mount")]
    public void CreatePlan_WithNonVolumeDataMount_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = ConfigurationStoreManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts =
            [
                source.Mounts[0] with { Kind = ResourceMountKind.Configuration },
            ],
        };
        var resource = new ConfigurationStoreResource(manifest);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        // Assert
        error.Message.ShouldContain("exactly one Volume mount named 'data'", Case.Sensitive);
    }

    private static PlanContext CreateContext(ConfigurationStoreResource resource)
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
            .ShouldBeTrue($"Plan output did not match golden fixture '{fixtureFileName}'. Actual: {actualJson}");
    }

    private sealed class TestApplicationEnvironment : IApplicationEnvironment
    {
        public EnvironmentName Name => "Development";

        public bool IsDevelopment => true;
    }
}
