using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

public class DatabasePlannerTests
{
    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - CreatePlan: matches the Database golden fixture")]
    public void CreatePlan_WithDatabaseManifest_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = DatabaseManifestFactory.Create();
        var resource = new DatabaseResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "database.json");
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - CreatePlan: applies replica and storage overrides to stable per-replica storage")]
    public void CreatePlan_WithTypedOverrides_ShouldApplyStableReplicaAndStorageSemantics()
    {
        // Arrange
        ResourceManifest source = DatabaseManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { MaxReplicas = 3 },
        };
        var options = new DatabaseResourceOptions { Replicas = 3 };
        options.Storage.Size = "30Gi";
        var resource = new DatabaseResource(manifest, options);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        plan.Workload.Kind.ShouldBe(WorkloadKind.StatefulSet);
        plan.Workload.Replicas.ShouldBe(3);
        plan.Workload.StableIdentity.ShouldBeTrue();

        VolumeSpec volume = plan.Volumes.ShouldHaveSingleItem();
        volume.Name.ShouldBe("data");
        volume.Size.ShouldBe("30Gi");
        volume.PerReplicaClaim.ShouldBeTrue();

        ServiceSpec governingService = plan.Services
            .Where(service => service.Headless && service.Governing)
            .ShouldHaveSingleItem();
        governingService.Endpoint.ShouldBeNull();
        governingService.Port.ShouldBeNull();
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - CreatePlan: rejects a database without persistent storage")]
    public void CreatePlan_WithoutVolumeMount_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = DatabaseManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts = Array.Empty<ResourceManifestMount>(),
        };
        var resource = new DatabaseResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("persistent Volume mount", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - CreatePlan: reserves the headless endpoint name for the governing service")]
    public void CreatePlan_WithHeadlessEndpointName_ShouldRejectDuplicateServiceIdentity()
    {
        ResourceManifest source = DatabaseManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Endpoints =
            [
                .. source.Endpoints,
                new ResourceManifestEndpoint
                {
                    Name = "headless",
                    Scheme = "cohesion-db",
                    Protocol = "tcp",
                    ContainerPort = 5741,
                },
            ],
        };
        var resource = new DatabaseResource(manifest);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(CreateContext(resource)));

        error.Message.ShouldContain("duplicate service name", Case.Sensitive);
        error.Message.ShouldContain("headless", Case.Sensitive);
    }

    private static PlanContext CreateContext(DatabaseResource resource)
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
