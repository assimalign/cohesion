using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class RemoteReferenceTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Manifest-less RemoteReference derives referenced endpoint names from static bindings")]
    public async Task Build_ManifestlessRemoteReference_ShouldDeclareStaticEndpointNames()
    {
        // Arrange
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create("api"));

        // Act
        IApplicationResourceDescriptor descriptor = builder.RemoteReference(
            "remote-cache",
            remote => remote.Endpoint("grpc", "https://cache.example.test:7443"));
        IApplicationModel model = builder.Build().Model;
        var external = descriptor.Resource.ShouldBeAssignableTo<IExternalResource>();
        ExternalResourceResolution resolution = await external.Resolver.ResolveAsync(
            new ExternalResourceResolutionContext(external.Declaration));

        // Assert
        external.Declaration.ReferencedEndpoints.ShouldBe(["grpc"]);
        resolution.Endpoints.ShouldHaveSingleItem().Name.ShouldBe("grpc");
        Plan(model, "remote-cache").Hints["cohesion.external.endpoints"].ShouldBe("grpc");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Remote external keeps provider-owned dependencies outside the consumer graph")]
    public void Build_RemoteExternalWithNestedBoundary_ShouldNotImportProviderDependency()
    {
        // Arrange
        ResourceManifest identity = TestManifestFactory.Create("identity-hub", "identity") with
        {
            References = [Reference("platform-secrets", "platform")],
        };
        var declaration = new ExternalResourceDeclaration(
            "identity-hub",
            "identity",
            ["control"],
            optional: false,
            identity);
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References = [Reference("identity-hub", "identity")],
        });
        builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("control", "https://identity.example.test:7443"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Resources.Select(resource => resource.Name.ToString()).ShouldBe(
            ["api", "identity-hub"],
            ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - External command-line binding overrides code")]
    public async Task Build_ExternalCommandLineBinding_ShouldOverrideCodeBinding()
    {
        // Arrange
        ResourceManifest externalManifest = TestManifestFactory.Create("identity-hub", "identity");
        var declaration = new ExternalResourceDeclaration(
            "identity-hub",
            "identity",
            ["https"],
            optional: false,
            manifest: externalManifest);
        IApplicationBuilder builder = Application.CreateBuilder(
                "appa",
                ["--external", "identity-hub=https=https://cli.example:7443"])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References =
            [
                new ResourceManifestReference
                {
                    Resource = "identity-hub",
                    Application = "identity",
                    Endpoints = ["https"],
                    Manifest = "Example.Identity.Manifest",
                },
            ],
        });
        builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("https", "https://code.example:4443"));

        // Act
        IApplicationModel model = builder.Build().Model;
        var external = model.Resources.OfType<IExternalResource>().ShouldHaveSingleItem();
        ExternalResourceResolution resolution = await external.Resolver.ResolveAsync(
            new ExternalResourceResolutionContext(external.Declaration));

        // Assert
        resolution.Resolved.ShouldBeTrue();
        ResourceEndpoint endpoint = resolution.Endpoints.ShouldHaveSingleItem();
        endpoint.Host.ShouldBe("cli.example");
        endpoint.Port.ShouldBe(7443);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Realize includes same-application closure only")]
    public void Build_RealizeExternal_ShouldIncludeOnlySameApplicationClosure()
    {
        // Arrange
        ResourceManifest cache = TestManifestFactory.Create("identity-cache", "identity");
        ResourceManifest platform = TestManifestFactory.Create("platform-secrets", "platform");
        ResourceManifest identity = TestManifestFactory.Create("identity-hub", "identity") with
        {
            References =
            [
                Reference("identity-cache", "identity"),
                Reference("platform-secrets", "platform"),
            ],
        };
        var declaration = new ExternalResourceDeclaration(
            "identity-hub",
            "identity",
            ["control"],
            optional: false,
            manifest: identity,
            closure: [identity, cache, platform]);
        IApplicationBuilder builder = Application.CreateBuilder(
                "appa",
                ["--environment", "Development", "--realize", "identity-hub"])
            .UseGateway(new FakeGateway("local"));
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References = [Reference("identity-hub", "identity")],
        });
        builder.AddExternal(declaration);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Resources.Select(resource => resource.Name.ToString()).ShouldBe(
            ["api", "identity-hub", "identity-cache", "platform-secrets"],
            ignoreOrder: true);
        ResourcePlan identityPlan = Plan(model, "identity-hub");
        ResourcePlan cachePlan = Plan(model, "identity-cache");
        ResourcePlan platformPlan = Plan(model, "platform-secrets");
        identityPlan.Hints.ContainsKey("cohesion.external").ShouldBeFalse();
        cachePlan.Hints.ContainsKey("cohesion.external").ShouldBeFalse();
        platformPlan.Hints["cohesion.external"].ShouldBe("true");
        identityPlan.Container.Environment[ResourceEnvironment.Application].ShouldBe("identity");
        cachePlan.Container.Environment[ResourceEnvironment.Application].ShouldBe("identity");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Realize replaces an inferred child external with its embedded manifest")]
    public void Build_RealizeClosureChildWasAlreadyInferred_ShouldUseEmbeddedManifest()
    {
        // Arrange
        ResourceManifest cache = TestManifestFactory.Create("identity-cache", "identity");
        ResourceManifest identity = TestManifestFactory.Create("identity-hub", "identity") with
        {
            References = [Reference("identity-cache", "identity")],
        };
        var declaration = new ExternalResourceDeclaration(
            "identity-hub",
            "identity",
            ["control"],
            optional: false,
            manifest: identity,
            closure: [identity, cache]);
        IApplicationBuilder builder = Application.CreateBuilder(
                "appa",
                ["--environment", "Development", "--realize", "identity-hub"])
            .UseGateway(new FakeGateway("local"));
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References = [Reference("identity-cache", "identity")],
        });
        builder.AddExternal(declaration);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        ResourceManifest realizedCache = model.Manifests.Single(
            manifest => manifest.Name == (ResourceName)"identity-cache");
        realizedCache.Application.ShouldBe((ApplicationName)"identity");
        realizedCache.Artifact.Assembly.ShouldBe(cache.Artifact.Assembly);
        realizedCache.ApplicationModel.ShouldBe(cache.ApplicationModel);
        Plan(model, "identity-cache").Hints.ContainsKey("cohesion.external").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Realize is Development only")]
    public void Build_RealizeExternalOutsideDevelopment_ShouldFail()
    {
        // Arrange
        ResourceManifest identity = TestManifestFactory.Create("identity-hub", "identity");
        var declaration = new ExternalResourceDeclaration(
            "identity-hub",
            "identity",
            ["control"],
            optional: false,
            manifest: identity,
            closure: [identity]);
        IApplicationBuilder builder = Application.CreateBuilder(
                "appa",
                ["--environment", "Production", "--realize", "identity-hub"])
            .UseGateway(new FakeGateway("local"));
        builder.AddResource(TestManifestFactory.Create("api"));
        builder.AddExternal(declaration);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("Development-only");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Required boundary reference wins when optional reference is declared first")]
    public void Build_OptionalBoundaryReferenceBeforeRequiredReference_ShouldMakeExternalRequired()
    {
        // Arrange
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create("optional-consumer") with
        {
            References = [Reference("identity-hub", "identity", "control", optional: true)],
        });
        builder.AddResource(TestManifestFactory.Create("required-consumer") with
        {
            References = [Reference("identity-hub", "identity", "control", optional: false)],
        });

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        IExternalResource external = model.Resources.OfType<IExternalResource>().ShouldHaveSingleItem();
        external.Declaration.Optional.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Duplicate boundary references union endpoints and retain explicit resolver")]
    public void Build_DisjointBoundaryReferences_ShouldUnionEndpointsAndRetainExplicitResolver()
    {
        // Arrange
        var resolver = new StubExternalResourceResolver();
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddExternal(
            new ExternalResourceDeclaration(
                "identity-hub",
                "identity",
                ["write"],
                optional: true),
            resolver);
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References = [Reference("identity-hub", "identity", "read", optional: true)],
        });

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        IExternalResource external = model.Resources.OfType<IExternalResource>().ShouldHaveSingleItem();
        external.Declaration.ReferencedEndpoints.ShouldBe(["read", "write"]);
        external.ShouldBeAssignableTo<IEndpointResource>()
            .Endpoints.Select(endpoint => endpoint.Name)
            .ShouldBe(["read", "write"]);
        external.Resolver.ShouldBeSameAs(resolver);
    }

    private static ResourceManifestReference Reference(string resource, string application) => new()
    {
        Resource = resource,
        Application = application,
        Endpoints = ["control"],
        Manifest = $"Example.{application}.{resource}.Manifest",
    };

    private static ResourceManifestReference Reference(
        string resource,
        string application,
        string endpoint,
        bool optional) => new()
    {
        Resource = resource,
        Application = application,
        Endpoints = [endpoint],
        Optional = optional,
        Manifest = $"Example.{application}.{resource}.Manifest",
    };

    private static ResourcePlan Plan(IApplicationModel model, string resource)
    {
        int index = model.Resources
            .Select((candidate, position) => (candidate, position))
            .Single(item => item.candidate.Name == (ResourceName)resource)
            .position;
        return model.Plans[index];
    }

    private sealed class StubExternalResourceResolver : IExternalResourceResolver
    {
        public ValueTask<ExternalResourceResolution> ResolveAsync(
            ExternalResourceResolutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExternalResourceResolution.Unresolved("Test resolver."));
    }
}
