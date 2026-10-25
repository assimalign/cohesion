using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceConfigurationTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: Build copies the deployment layer into the plan")]
    public void Build_WithConfiguredSection_ShouldCarryTheDeploymentLayer()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest())
            .Configure("Web:Cors", """{"defaultPolicy":{"origins":["https://portal.example.com"]}}""");

        // Act
        ResourcePlan plan = builder.Build().Model.Plans[0];

        // Assert
        plan.Configuration.Keys.ShouldBe(["Web:Cors"]);
        plan.Configuration["Web:Cors"].GetProperty("defaultPolicy").GetProperty("origins")[0].GetString()
            .ShouldBe("https://portal.example.com");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: patches for one section compose in call order and keep tombstones")]
    public void Configure_SameSectionTwice_ShouldComposeThePatches()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest())
            .Configure("Web:Cors", """{"defaultPolicy":{"origins":["https://a.example.com"],"maxAgeSeconds":60}}""")
            .Configure("Web:Cors", """{"defaultPolicy":{"maxAgeSeconds":null},"policies":{"partners":{"origins":[]}}}""");

        // Act
        JsonElement patch = builder.Build().Model.Plans[0].Configuration["Web:Cors"];

        // Assert
        JsonElement policy = patch.GetProperty("defaultPolicy");
        policy.GetProperty("origins")[0].GetString().ShouldBe("https://a.example.com");
        policy.GetProperty("maxAgeSeconds").ValueKind.ShouldBe(JsonValueKind.Null);
        patch.GetProperty("policies").GetProperty("partners").GetProperty("origins").GetArrayLength().ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: an unknown section fails Build with the resource and section named")]
    public void Build_WithUnknownSection_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest()).Configure("Web:Corz", "{}");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'api'", Case.Sensitive);
        exception.Message.ShouldContain("'Web:Corz'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: a patch that would not bind fails Build with its path named")]
    public void Build_WithInvalidPatch_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest())
            .Configure("Orders:Limits", """{"maxItems":"ten"}""");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("'Orders:Limits'", Case.Sensitive);
        exception.Message.ShouldContain("'$.maxItems'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: a resource whose build described no surface cannot be configured")]
    public void Build_WithoutSurface_ShouldFail()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create("api")).Configure("Web:Cors", "{}");

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("resource.runtime.json", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Configure: a built model is immutable")]
    public void Configure_OnBuiltDescriptor_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest());
        IApplicationResourceDescriptor built = builder.Build().Model.Descriptors[0];

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => built.Configure("Web:Cors", "{}"));
        Should.Throw<InvalidOperationException>(() => built.SetExtension("kubernetes/hpa", JsonDocument.Parse("{}").RootElement));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Configure: invalid arguments are refused before Build")]
    [InlineData("Web Cors", "{}")]
    [InlineData(":Web", "{}")]
    [InlineData("Web:Cors", "{not json")]
    public void Configure_WithInvalidArgument_ShouldThrowArgumentException(string section, string patch)
    {
        // Arrange
        IApplicationResourceDescriptor descriptor = CreateBuilder().AddResource(TestSurfaceFactory.CreateManifest());

        // Act & Assert
        Should.Throw<ArgumentException>(() => descriptor.Configure(section, patch));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Extensions: platform data travels in the plan and reads back")]
    public void SetExtension_ThenBuild_ShouldCarryTheData()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor descriptor = builder.AddResource(TestSurfaceFactory.CreateManifest());
        using JsonDocument hpa = JsonDocument.Parse("""{"minReplicas":2,"maxReplicas":10}""");
        descriptor.SetExtension("kubernetes/hpa", hpa.RootElement);

        // Act
        IApplicationResourceDescriptor built = builder.Build().Model.Descriptors[0];

        // Assert
        descriptor.TryGetExtension("kubernetes/hpa", out JsonElement authoring).ShouldBeTrue();
        authoring.GetProperty("minReplicas").GetInt32().ShouldBe(2);
        built.TryGetExtension("kubernetes/hpa", out JsonElement planned).ShouldBeTrue();
        planned.GetProperty("maxReplicas").GetInt32().ShouldBe(10);
        built.Plan!.Extensions.Keys.ShouldBe(["kubernetes/hpa"]);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Extensions: a key is '<platform>/<name>' in lowercase words")]
    [InlineData("kubernetes")]
    [InlineData("Kubernetes/hpa")]
    [InlineData("kubernetes/hpa/v2")]
    [InlineData("/hpa")]
    public void SetExtension_WithInvalidKey_ShouldThrow(string key)
    {
        // Arrange
        IApplicationResourceDescriptor descriptor = CreateBuilder().AddResource(TestSurfaceFactory.CreateManifest());

        // Act & Assert
        Should.Throw<ArgumentException>(() => descriptor.SetExtension(key, JsonDocument.Parse("{}").RootElement));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Plan: configuration and extensions round-trip, and an empty plan omits both")]
    public void Serialize_PlanWithDeploymentData_ShouldRoundTripAndOmitWhenEmpty()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor configured = builder.AddResource(TestSurfaceFactory.CreateManifest("api"))
            .Configure("Orders:Limits", """{"maxItems":10}""");
        configured.SetExtension("kubernetes/hpa", JsonDocument.Parse("""{"maxReplicas":4}""").RootElement);
        builder.AddResource(TestSurfaceFactory.CreateManifest("worker"));
        IApplicationModel model = builder.Build().Model;

        // Act
        string configuredJson = JsonSerializer.Serialize(model.Plans[0], ResourcePlanJsonContext.Default.ResourcePlan);
        string plainJson = JsonSerializer.Serialize(model.Plans[1], ResourcePlanJsonContext.Default.ResourcePlan);
        ResourcePlan reparsed = JsonSerializer.Deserialize(configuredJson, ResourcePlanJsonContext.Default.ResourcePlan)!;

        // Assert
        plainJson.ShouldNotContain("\"configuration\"", Case.Sensitive);
        plainJson.ShouldNotContain("\"extensions\"", Case.Sensitive);
        reparsed.Configuration["Orders:Limits"].GetProperty("maxItems").GetInt32().ShouldBe(10);
        reparsed.Extensions["kubernetes/hpa"].GetProperty("maxReplicas").GetInt32().ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Document: the model document carries each surface beside its manifest")]
    public void ToModel_FromSerializedDocument_ShouldKeepTheSurfaceAndDeploymentLayer()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestSurfaceFactory.CreateManifest()).Configure("Web:Cors", """{"defaultPolicy":{"maxAgeSeconds":5}}""");
        IApplicationModel model = builder.Build().Model;
        using var stream = new System.IO.MemoryStream();
        ApplicationModelDocument.Create(model).Save(stream);
        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        // Act
        IApplicationModel restored = ApplicationModelDocument.Parse(json).ToModel();

        // Assert
        restored.Manifests[0].Surface.ShouldNotBeNull().Digest.ShouldBe(model.Manifests[0].Surface!.Digest);
        restored.Plans[0].Configuration["Web:Cors"].GetProperty("defaultPolicy").GetProperty("maxAgeSeconds").GetInt32().ShouldBe(5);
    }

    private static IApplicationBuilder CreateBuilder() =>
        Application.CreateBuilder((ApplicationName)"appa", ["--environment=Development"]).UseGateway(new FakeGateway());
}
