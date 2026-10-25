using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ScaleBoundsTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Scale: Build copies the bounds into the plan's workload")]
    public void Build_WithScaleBounds_ShouldCarryThemInThePlan()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create()).ScaleBetween(2, 3);

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];
        string json = JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan);
        ResourcePlan? parsed = JsonSerializer.Deserialize(json, ResourcePlanJsonContext.Default.ResourcePlan);

        // Assert
        plan.Workload.Scale.ShouldBe(new ScaleBounds(2, 3));
        json.ShouldContain("\"scale\":{\"minimum\":2,\"maximum\":3}", Case.Sensitive);
        parsed.ShouldNotBeNull();
        parsed.Workload.Scale.ShouldBe(new ScaleBounds(2, 3));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Scale: a plan without bounds serializes as before")]
    public void Build_WithoutScaleBounds_ShouldOmitTheMember()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create());

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];

        // Assert
        plan.Workload.Scale.ShouldBeNull();
        JsonSerializer.Serialize(plan, ResourcePlanJsonContext.Default.ResourcePlan).ShouldNotContain("\"scale\"", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Scale: ScaleBetween refuses an empty or inverted range")]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    public void ScaleBetween_InvalidRange_ShouldThrow(int minimum, int maximum)
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddResource(TestManifestFactory.Create());

        // Act
        Action scale = () => descriptor.ScaleBetween(minimum, maximum);

        // Assert
        scale.ShouldThrow<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Scale: a maximum above the manifest's maxReplicas fails Build")]
    public void Build_ScaleAboveManifestCeiling_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create(maxReplicas: 3)).ScaleBetween(1, 4);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'worker'", Case.Sensitive);
        exception.Message.ShouldContain("maxReplicas", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Scale: a job does not autoscale, so bounds fail Build")]
    public void Build_ScaleBoundsOnJob_ShouldFail()
    {
        // Arrange
        ResourceManifest manifest = TestManifestFactory.Create();
        manifest = manifest with { Lifecycle = manifest.Lifecycle with { Workload = WorkloadKind.Job } };
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(manifest).ScaleBetween(1, 2);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'Job'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Scale: TryGetScale reads authoring bounds and then the plan's")]
    public void TryGetScale_BeforeAndAfterBuild_ShouldReadTheDeclaredBounds()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddResource(TestManifestFactory.Create()).ScaleBetween(1, 2);

        // Act
        bool declared = descriptor.TryGetScale(out ScaleBounds? authoring);
        IApplicationModel model = builder.Build().Model;
        bool built = model.Descriptors[0].TryGetScale(out ScaleBounds? planned);

        // Assert
        declared.ShouldBeTrue();
        authoring.ShouldBe(new ScaleBounds(1, 2));
        built.ShouldBeTrue();
        planned.ShouldBe(new ScaleBounds(1, 2));
    }

    private static IApplicationBuilder CreateBuilder() =>
        Application.CreateBuilder((ApplicationName)"appa", ["--environment=Development"]).UseGateway(new FakeGateway());
}
