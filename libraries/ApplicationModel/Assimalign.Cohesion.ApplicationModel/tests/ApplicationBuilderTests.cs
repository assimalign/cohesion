using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationBuilderTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Command-line options support split and equals forms")]
    [InlineData("--mode", "describe", "--gateway", "fake", "--environment", "Development", "--adopt", "--restart-orphans")]
    [InlineData("--mode", "describe", "--gateway", "fake", "--environment", "Development", "--adopt", "true", "--restart-orphans", "true")]
    [InlineData("--mode=describe", "--gateway=fake", "--environment=Development", "--adopt=true", "--restart-orphans=true")]
    public void CreateBuilder_CommandLineOptions_AreCarriedByModel(params string[] args)
    {
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), args)
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        builder.RunMode.ShouldBe(GatewayRunMode.Describe);
        builder.RequestedGateway.ShouldBe((ResourceName)"fake");
        builder.Environment.Name.ShouldBe((EnvironmentName)"Development");

        IApplicationModel model = builder.Build().Model;

        model.RunMode.ShouldBe(GatewayRunMode.Describe);
        model.GatewayIdentity.ShouldBe((ResourceName)"fake");
        model.Environment.Name.ShouldBe((EnvironmentName)"Development");
        model.Environment.IsDevelopment.ShouldBeTrue();
        model.Adopt.ShouldBeTrue();
        model.RestartOrphans.ShouldBeTrue();
        model.Owner.ShouldBe("appa@fake");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - UseName overrides the legacy default")]
    public void UseName_OverridesDefaultName()
    {
        IApplicationBuilder builder = Application.CreateBuilder()
            .UseName("orders")
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        builder.Build().Model.Name.ShouldBe((ApplicationName)"orders");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Build rejects invalid RFC1123 application names")]
    [InlineData("")]
    [InlineData("AppA")]
    [InlineData("-appa")]
    [InlineData("appa-")]
    [InlineData("app_a")]
    public void Build_InvalidApplicationName_Throws(string name)
    {
        IApplicationBuilder builder = Application.CreateBuilder()
            .UseName(name)
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("RFC1123");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build rejects an empty realized resource set")]
    public void Build_WithoutResources_ThrowsDesignMessage()
    {
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), []);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldBe(
            "every reference crossed an application boundary; declare CohesionApplication or reference a resource of appa");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - AddResource returns the resource descriptor")]
    public void AddResource_WithResource_ReturnsDescriptorWrappingResource()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        var resource = new FakeResource("dns");

        IApplicationResourceDescriptor descriptor = builder.AddResource(resource);

        descriptor.ShouldNotBeNull();
        descriptor.Resource.ShouldBeSameAs(resource);
        descriptor.Plan.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - AddResource rejects duplicate resource names")]
    public void AddResource_DuplicateName_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        Should.Throw<InvalidOperationException>(() => builder.AddResource(new FakeResource("dns")));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build requires a selected gateway")]
    public void Build_WithoutGateway_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder();
        builder.AddResource(new FakeResource("dns"));

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build produces an application for a legacy resource")]
    public void Build_WithGateway_Succeeds()
    {
        var gateway = new FakeGateway();
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new FakeResource("dns"));

        IApplication app = builder.Build();

        app.ShouldNotBeNull();
        app.Model.Resources.Count.ShouldBe(1);
        app.Model.Manifests.Count.ShouldBe(1);
        app.Model.Plans.Count.ShouldBe(1);
        app.Model.Descriptors[0].Plan.ShouldBeSameAs(app.Model.Plans[0]);
        gateway.ValidatedModel.ShouldBeSameAs(app.Model);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build rejects a circular dependency")]
    public void Build_WithCircularDependency_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new FakeResource("b"));
        a.DependsOn(b);
        b.DependsOn(a);

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build infers manifest dependency edges")]
    public void Build_ManifestReference_InfersDependencyEdge()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("database")],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        var consumerResource = new CountingPlannedResource(consumer);
        builder.AddResource(consumerResource);
        builder.AddResource(TestManifestFactory.Create("database"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        IApplicationResourceDescriptor dependency = model.Descriptors[0].Dependencies.ShouldHaveSingleItem();
        dependency.Resource.Name.ShouldBe((ResourceName)"database");
        consumerResource.LastReferences.ShouldNotBeNull()["database"].Name.ShouldBe((ResourceName)"database");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Explicit dependency edges stay additive to inferred edges")]
    public void Build_ExplicitAndManifestReferences_MergeDependencyEdges()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("database")],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        var consumerResource = new CountingPlannedResource(consumer);
        IApplicationResourceDescriptor consumerDescriptor = builder.AddResource(consumerResource);
        IApplicationResourceDescriptor database = builder.AddResource(TestManifestFactory.Create("database"));
        IApplicationResourceDescriptor cache = builder.AddResource(TestManifestFactory.Create("cache"));
        consumerDescriptor.DependsOn(database, cache);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Descriptors[0].Dependencies.Count.ShouldBe(2);
        model.Descriptors[0].Dependencies[0].Resource.Name.ShouldBe((ResourceName)"database");
        model.Descriptors[0].Dependencies[1].Resource.Name.ShouldBe((ResourceName)"cache");
        consumerResource.LastReferences.ShouldNotBeNull().Keys.ShouldHaveSingleItem().ShouldBe("database");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Optional missing manifest references add no edge")]
    public void Build_OptionalMissingManifestReference_AddsNoDependencyEdge()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("metrics", optional: true)],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(consumer);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Descriptors.ShouldHaveSingleItem().Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Optional present manifest references remain non-gating")]
    public void Build_OptionalPresentManifestReference_AddsNoDependencyEdge()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("metrics", optional: true)],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        var consumerResource = new CountingPlannedResource(consumer);
        builder.AddResource(consumerResource);
        builder.AddResource(TestManifestFactory.Create("metrics"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Descriptors[0].Dependencies.ShouldBeEmpty();
        consumerResource.LastReferences.ShouldNotBeNull()["metrics"].Name.ShouldBe((ResourceName)"metrics");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Cross-application references remain external")]
    public void Build_RequiredCrossApplicationReference_AddsNoLocalEdge()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("configuration-store", application: "platform")],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(consumer);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Descriptors.ShouldHaveSingleItem().Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Required missing manifest references fail Build")]
    public void Build_RequiredMissingManifestReference_ThrowsActionableError()
    {
        // Arrange
        ResourceManifest consumer = TestManifestFactory.Create("api") with
        {
            References = [CreateReference("database")],
        };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(consumer);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        error.Message.ShouldContain("appa/database");
        error.Message.ShouldContain("mark the reference optional");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Model projects descriptors and resources one-to-one")]
    public void Build_Model_ProjectsResourcesOneToOneWithDescriptors()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("a"));
        builder.AddResource(new FakeResource("b"));

        IApplicationModel model = builder.Build().Model;

        model.Resources.Count.ShouldBe(model.Descriptors.Count);
        for (int i = 0; i < model.Descriptors.Count; i++)
        {
            model.Resources[i].ShouldBeSameAs(model.Descriptors[i].Resource);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Owner guard refuses a foreign owner without adopt")]
    public void AssertOwner_ForeignOwnerWithoutAdopt_ThrowsActionableError()
    {
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));
        IApplicationModel model = builder.Build().Model;

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => model.AssertOwner("other@kubernetes"));

        error.Message.ShouldContain("appa@fake");
        error.Message.ShouldContain("other@kubernetes");
        error.Message.ShouldContain("--adopt");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Owner guard permits an explicit adopt")]
    public void AssertOwner_ForeignOwnerWithAdopt_Succeeds()
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--adopt"])
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));
        IApplicationModel model = builder.Build().Model;

        Should.NotThrow(() => model.AssertOwner("other@kubernetes"));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Args builder requires a name outside Development")]
    public void Build_ArgsBuilderWithoutNameOutsideDevelopment_ThrowsActionableError()
    {
        IApplicationBuilder builder = Application.CreateBuilder(["--environment", "Production"])
            .UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("Application.CreateBuilder(ApplicationName, args)");
        error.Message.ShouldContain("UseName");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Args builder rejects an unknown run mode")]
    public void CreateBuilder_WithUnknownRunMode_Throws()
    {
        ArgumentException error = Should.Throw<ArgumentException>(
            () => Application.CreateBuilder(["--mode=launch"]));

        error.Message.ShouldContain("run, apply, teardown, bootstrap, describe, or render");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - AddResource defers custom planning until Build")]
    public void AddResource_PlannedResource_ComputesAndValidatesPlanAtBuild()
    {
        ResourceManifest manifest = TestManifestFactory.Create();
        var resource = new CountingPlannedResource(manifest);
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());

        builder.AddResource(resource);

        resource.PlanCount.ShouldBe(0);

        IApplicationModel model = builder.Build().Model;

        resource.PlanCount.ShouldBe(1);
        model.Manifests.ShouldHaveSingleItem().Name.ShouldBe(manifest.Name);
        model.Plans.ShouldHaveSingleItem().Resource.ShouldBe((ResourceName)"worker");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - AddResource and Build snapshot mutable authoring inputs")]
    public void Build_WithMutableAuthoringInputs_ReturnsImmutableSnapshot()
    {
        var endpoints = new List<ResourceManifestEndpoint>
        {
            new()
            {
                Name = "control",
                Scheme = "http",
                Protocol = "tcp",
                ContainerPort = 8080,
            },
        };
        ResourceManifest manifest = TestManifestFactory.Create() with { Endpoints = endpoints };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        IApplicationResourceDescriptor first = builder.AddResource(manifest);
        IApplicationResourceDescriptor second = builder.AddResource(
            TestManifestFactory.Create("dependency"));

        endpoints.Clear();
        IApplicationModel model = builder.Build().Model;
        first.DependsOn(second);

        model.Descriptors[0].Dependencies.ShouldBeEmpty();
        ((IEndpointResource)model.Resources[0]).Endpoints.ShouldHaveSingleItem();
        model.Manifests[0].Endpoints.ShouldHaveSingleItem();
        model.Plans[0].Container.Ports.ShouldHaveSingleItem();
        Should.Throw<InvalidOperationException>(() => model.Descriptors[0].DependsOn(model.Descriptors[1]));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Generic manifest resource applies typed options at Build")]
    public void AddResource_WithManifestAndOptions_UsesGenericPlannerAtBuild()
    {
        ResourceManifest manifest = TestManifestFactory.Create();
        var options = new ResourceOptions { Replicas = 2 };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());

        IApplicationResourceDescriptor descriptor = builder.AddResource(manifest, options);

        descriptor.Resource.ShouldBeAssignableTo<IPlannedResource>();
        IApplicationModel model = builder.Build().Model;
        model.Plans.ShouldHaveSingleItem().Workload.Replicas.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build validates planned replica overrides")]
    public void Build_WithReplicaOverrideAboveManifestMaximum_Throws()
    {
        ResourceManifest manifest = TestManifestFactory.Create(maxReplicas: 2);
        var options = new ResourceOptions { Replicas = 3 };
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(manifest, options);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("maxReplicas");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build rejects a custom plan that spoofs frozen runtime identity")]
    public void Build_WithSpoofedPlanEnvironment_Throws()
    {
        ResourceManifest manifest = TestManifestFactory.Create();
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(new InvalidEnvironmentPlannedResource(manifest));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("COHESION_APPLICATION", Case.Sensitive);
        error.Message.ShouldContain("appa", Case.Sensitive);
    }

    private static ResourceManifestReference CreateReference(
        string resource,
        bool optional = false,
        string application = "appa") => new()
        {
            Resource = resource,
            Application = application,
            Endpoints = ["control"],
            Optional = optional,
            Manifest = $"Example.{resource}.Manifest",
        };
}
