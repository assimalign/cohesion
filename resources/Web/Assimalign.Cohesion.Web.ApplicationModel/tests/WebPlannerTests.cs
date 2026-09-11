using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.ApplicationModel.Tests;

public sealed class WebPlannerTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - CreatePlan: matches the Web golden fixture")]
    public void CreatePlan_WithWebManifestAndOptions_ShouldMatchGoldenFixture()
    {
        // Arrange
        ResourceManifest manifest = WebManifestFactory.Create();
        var options = new WebResourceOptions { Replicas = 3 };
        var resource = new WebResource(manifest, options);
        PlanContext context = CreateContext(resource);

        // Act
        ResourcePlan plan = resource.CreatePlan(context);

        // Assert
        ShouldMatchFixture(plan, "web.json");
        plan.Workload.Kind.ShouldBe(WorkloadKind.Deployment);
        plan.Workload.Replicas.ShouldBe(3);
        plan.Workload.StableIdentity.ShouldBeFalse();
        plan.Volumes.ShouldBeEmpty();
        plan.Services.Count.ShouldBe(manifest.Endpoints.Count);
        Should.NotThrow(() => ResourcePlanValidator.Validate(plan, context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - CreatePlan: rejects a non-Web manifest")]
    public void CreatePlan_WithNonWebManifest_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = WebManifestFactory.Create() with { Kind = "Database" };
        var resource = new WebResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("cannot plan", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - CreatePlan: rejects a stateful workload")]
    public void CreatePlan_WithNonDeploymentWorkload_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = WebManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Lifecycle = source.Lifecycle with { Workload = WorkloadKind.StatefulSet },
        };
        var resource = new WebResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("stateless Deployment", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - CreatePlan: rejects persistent volume mounts")]
    public void CreatePlan_WithVolumeMount_ShouldThrow()
    {
        // Arrange
        ResourceManifest source = WebManifestFactory.Create();
        ResourceManifest manifest = source with
        {
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "data",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/data",
                    Size = "1Gi",
                },
            ],
        };
        var resource = new WebResource(manifest);
        PlanContext context = CreateContext(resource);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("cannot declare a Volume mount", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - CreatePlan: rejects a storage override")]
    public void CreatePlan_WithStorageOverride_ShouldThrow()
    {
        // Arrange
        ResourceManifest manifest = WebManifestFactory.Create();
        var options = new WebResourceOptions();
        options.Storage.Size = "1Gi";
        var resource = new WebResource(manifest, options);
        PlanContext context = CreateContext(resource);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("stateless", Case.Sensitive);
    }

    private static PlanContext CreateContext(WebResource resource)
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
