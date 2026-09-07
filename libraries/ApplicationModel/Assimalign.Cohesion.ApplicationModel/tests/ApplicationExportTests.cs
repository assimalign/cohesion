using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationExportTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Model document preserves portable RemoteReference bindings")]
    [InlineData("static", "https://peer.example.test:7443")]
    [InlineData("file", "peer-export.json")]
    [InlineData("gateway", "https://gateway.example.test:8443/")]
    public async Task ApplicationModelDocument_RemoteReferenceBinding_ShouldRoundTripDeclarationAndResolver(
        string binding,
        string location)
    {
        // Arrange
        ResourceManifest cache = TestManifestFactory.Create("peer-cache", "peer");
        ResourceManifest peer = TestManifestFactory.Create("peer-api", "peer") with
        {
            References =
            [
                new ResourceManifestReference
                {
                    Resource = "peer-cache",
                    Application = "peer",
                    Endpoints = ["control"],
                    Manifest = "Example.Peer.Cache.Manifest",
                },
            ],
        };
        var declaration = new ExternalResourceDeclaration(
            "peer-api",
            "peer",
            ["control"],
            optional: false,
            peer,
            [peer, cache]);
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create("api"));
        builder.RemoteReference(
            declaration,
            remote =>
            {
                if (binding == "static")
                {
                    remote.Endpoint("control", location);
                }
                else if (binding == "file")
                {
                    remote.File(location);
                }
                else
                {
                    remote.Gateway(location);
                }
            });

        // Act
        ApplicationModelDocument document = ApplicationModelDocument.Create(builder.Build().Model);
        using var stream = new MemoryStream();
        document.Save(stream);
        ApplicationModelDocument parsed = ApplicationModelDocument.Parse(
            Encoding.UTF8.GetString(stream.ToArray()));
        IApplicationModel restored = parsed.ToModel();
        var restoredExternal = restored.Resources.OfType<IExternalResource>().ShouldHaveSingleItem();

        // Assert
        ApplicationModelExternalDocument serialized = parsed.Resources
            .Single(resource => resource.Name == "peer-api")
            .External.ShouldNotBeNull();
        serialized.Application.ShouldBe("peer");
        serialized.Binding.ShouldBe(binding);
        serialized.Closure.Select(manifest => manifest.Name.ToString()).ShouldBe(
            ["peer-api", "peer-cache"]);
        restoredExternal.Declaration.Closure.Select(manifest => manifest.Name.ToString()).ShouldBe(
            ["peer-api", "peer-cache"]);

        if (binding == "static")
        {
            ExternalResourceResolution resolution = await restoredExternal.Resolver.ResolveAsync(
                new ExternalResourceResolutionContext(restoredExternal.Declaration));
            resolution.Resolved.ShouldBeTrue();
            resolution.Endpoints.ShouldHaveSingleItem().Host.ShouldBe("peer.example.test");
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - File resolver rejects a same-named resource owned by another application")]
    public async Task FileResolver_ExportResourceHasWrongApplication_ShouldRemainUnresolved()
    {
        // Arrange
        IApplicationBuilder providerBuilder = Application
            .CreateBuilder(ApplicationName.Parse("other"), [])
            .UseGateway(new FakeGateway());
        providerBuilder.AddResource(TestManifestFactory.Create("peer-api", "other"));
        ApplicationExportDocument export = ApplicationExportDocument.Create(
            providerBuilder.Build().Model,
            "1.0.0",
            new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>
            {
                ["peer-api"] =
                [
                    new ApplicationExportEndpoint(
                        "control",
                        "https://wrong.example.test:7443"),
                ],
            });
        string path = Path.GetTempFileName();

        try
        {
            export.Save(path);
            var declaration = new ExternalResourceDeclaration(
                "peer-api",
                "peer",
                ["control"],
                optional: false,
                TestManifestFactory.Create("peer-api", "peer"));
            IApplicationBuilder consumerBuilder = Application
                .CreateBuilder(ApplicationName.Parse("appa"), [])
                .UseGateway(new FakeGateway());
            consumerBuilder.AddResource(TestManifestFactory.Create("api"));
            consumerBuilder.RemoteReference(declaration, remote => remote.File(path));
            var external = consumerBuilder.Build().Model.Resources
                .OfType<IExternalResource>()
                .ShouldHaveSingleItem();

            // Act
            ExternalResourceResolution resolution = await external.Resolver.ResolveAsync(
                new ExternalResourceResolutionContext(external.Declaration));

            // Assert
            resolution.Resolved.ShouldBeFalse();
            resolution.Detail.ShouldNotBeNull();
            resolution.Detail!.ShouldContain("is not 'peer'");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Manifest-less file reference accepts the provider application identity")]
    public async Task FileResolver_ManifestlessReference_ShouldResolveProviderResource()
    {
        // Arrange
        IApplicationBuilder providerBuilder = Application
            .CreateBuilder(ApplicationName.Parse("peer"), [])
            .UseGateway(new FakeGateway());
        providerBuilder.AddResource(TestManifestFactory.Create("legacy-api", "peer"));
        ApplicationExportDocument export = ApplicationExportDocument.Create(
            providerBuilder.Build().Model,
            "1.0.0",
            new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>
            {
                ["legacy-api"] =
                [
                    new ApplicationExportEndpoint(
                        "control",
                        "https://legacy.example.test:7443"),
                ],
            });
        string path = Path.GetTempFileName();

        try
        {
            export.Save(path);
            IApplicationBuilder consumerBuilder = Application
                .CreateBuilder(ApplicationName.Parse("appa"), [])
                .UseGateway(new FakeGateway());
            consumerBuilder.AddResource(TestManifestFactory.Create("api"));
            consumerBuilder.RemoteReference("legacy-api", remote => remote.File(path));
            var external = consumerBuilder.Build().Model.Resources
                .OfType<IExternalResource>()
                .ShouldHaveSingleItem();

            // Act
            ExternalResourceResolution resolution = await external.Resolver.ResolveAsync(
                new ExternalResourceResolutionContext(external.Declaration));

            // Assert
            resolution.Resolved.ShouldBeTrue();
            resolution.Endpoints.ShouldHaveSingleItem().Host.ShouldBe("legacy.example.test");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application export publishes local resources while retaining external nodes in its model")]
    public void ApplicationExportDocument_CreateWithExternal_ShouldExcludeItFromDiscoveryResources()
    {
        // Arrange
        ResourceManifest peer = TestManifestFactory.Create("peer-api", "peer");
        var declaration = new ExternalResourceDeclaration(
            "peer-api",
            "peer",
            ["control"],
            optional: false,
            peer);
        IApplicationBuilder builder = Application
            .CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway());
        builder.RemoteReference(
            declaration,
            remote => remote.Endpoint("control", "https://peer.example.test:7443"));
        builder.AddResource(TestManifestFactory.Create("api") with
        {
            References =
            [
                new ResourceManifestReference
                {
                    Resource = "peer-api",
                    Application = "peer",
                    Endpoints = ["control"],
                    Manifest = "Example.Peer.Api.Manifest",
                },
            ],
        });
        IApplicationModel model = builder.Build().Model;

        // Act
        ApplicationExportDocument export = ApplicationExportDocument.Create(model, "1.0.0");
        using var stream = new MemoryStream();
        export.Save(stream);
        ApplicationExportDocument restored = ApplicationExportDocument.Parse(
            Encoding.UTF8.GetString(stream.ToArray()));

        // Assert
        restored.Resources.ShouldHaveSingleItem().Name.ShouldBe("api");
        restored.Model.Resources.Count.ShouldBe(2);
        restored.ToModel().Resources.ShouldContain(resource => resource is IExternalResource);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Model document round-trips into a runnable immutable model")]
    public void ApplicationModelDocument_SaveParseToModel_ShouldPreserveGraphAndAllowGatewayOverrides()
    {
        // Arrange
        IApplicationModel source = CreateModel();
        ApplicationModelDocument document = ApplicationModelDocument.Create(source);

        // Act
        using var stream = new MemoryStream();
        document.Save(stream);
        string json = Encoding.UTF8.GetString(stream.ToArray());
        ApplicationModelDocument parsed = ApplicationModelDocument.Parse(json);
        IApplicationModel restored = parsed.ToModel(GatewayRunMode.Apply, "application-set");

        // Assert
        parsed.Schema.ShouldBe(ApplicationModelDocument.CurrentSchema);
        restored.Name.ShouldBe(source.Name);
        restored.Environment.Name.ShouldBe(source.Environment.Name);
        restored.RunMode.ShouldBe(GatewayRunMode.Apply);
        restored.GatewayIdentity.ShouldBe((ResourceName)"application-set");
        restored.Owner.ShouldBe("appa@application-set");
        restored.Descriptors.Count.ShouldBe(2);
        restored.Descriptors[1].Dependencies.Count.ShouldBe(1);
        restored.Descriptors[1].Dependencies[0].ShouldBeSameAs(restored.Descriptors[0]);
        restored.Manifests[1].Name.ShouldBe((ResourceName)"api");
        restored.Plans[1].Resource.ShouldBe((ResourceName)"api");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application export round-trips its model, hashes, endpoints and trust key")]
    public void ApplicationExportDocument_CreateSaveLoad_ShouldRoundTripPublicContract()
    {
        // Arrange
        IApplicationModel source = CreateModel();
        var endpoints = new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>
        {
            ["database"] =
            [
                new ApplicationExportEndpoint(
                    "control",
                    "http://database.appa.svc:8080",
                    "https://database.example.com"),
            ],
            ["api"] =
            [
                new ApplicationExportEndpoint("control", "http://api.appa.svc:8080"),
            ],
        };
        using JsonDocument trustKeyDocument = JsonDocument.Parse(
            """{"kty":"EC","crv":"P-256","x":"example-x","y":"example-y"}""");
        ApplicationExportDocument export = ApplicationExportDocument.Create(
            source,
            "1.2.3",
            endpoints,
            trustKeyDocument.RootElement);
        string path = Path.GetTempFileName();

        try
        {
            // Act
            export.Save(path);
            ApplicationExportDocument loaded = ApplicationExportDocument.Load(path);
            IApplicationModel restored = loaded.ToModel();

            // Assert
            loaded.SchemaVersion.ShouldBe(ApplicationExportDocument.CurrentSchemaVersion);
            loaded.Application.ShouldBe("appa");
            loaded.Environment.ShouldBe("Development");
            loaded.Version.ShouldBe("1.2.3");
            loaded.TrustKey.ShouldNotBeNull();
            loaded.TrustKey.Value.GetProperty("kty").GetString().ShouldBe("EC");
            loaded.Resources.Count.ShouldBe(2);
            loaded.Resources[0].ManifestHash.ShouldBe(
                ResourceManifestCanonicalizer.ComputeHash(source.Manifests[0]));
            loaded.Resources[0].Endpoints[0].Internal.ShouldBe("http://database.appa.svc:8080");
            loaded.Resources[0].Endpoints[0].Public.ShouldBe("https://database.example.com");
            loaded.Model.Schema.ShouldBe(ApplicationModelDocument.CurrentSchema);
            restored.Descriptors[1].Dependencies[0].Resource.Name.ShouldBe((ResourceName)"database");

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
            json.RootElement.GetProperty("model").GetProperty("application").GetString().ShouldBe("appa");
            json.RootElement.GetProperty("trustKey").GetProperty("crv").GetString().ShouldBe("P-256");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Application export rejects null embedded model resources with a typed data error")]
    public void ApplicationExportDocument_ModelContainsNullResource_ShouldThrowInvalidData()
    {
        // Arrange
        ApplicationModelDocument valid = ApplicationModelDocument.Create(CreateModel());
        var invalidModel = new ApplicationModelDocument(
            valid.Schema,
            valid.Application,
            valid.Environment,
            valid.Gateway,
            valid.Owner,
            valid.Mode,
            valid.Adopt,
            valid.RestartOrphans,
            [null!]);
        var export = new ApplicationExportDocument(
            ApplicationExportDocument.CurrentSchemaVersion,
            valid.Application,
            valid.Environment,
            "1.0.0",
            trustKey: null,
            resources: [],
            invalidModel);
        using var stream = new MemoryStream();

        // Act
        InvalidDataException error = Should.Throw<InvalidDataException>(() => export.Save(stream));

        // Assert
        error.Message.ShouldContain("must not contain null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Canonical manifest hashes ignore order and local artifact paths")]
    public void ResourceManifestCanonicalizer_ComputeHash_ShouldUsePortableDeterministicContract()
    {
        // Arrange
        ResourceManifest first = TestManifestFactory.Create() with
        {
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.Worker",
                Project = @"C:\src\one\Example.Worker.csproj",
                AppHost = @"C:\src\one\bin\Example.Worker.exe",
                Image = "example/worker@sha256:1234",
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["B"] = "two",
                ["A"] = "one",
            },
            Properties = new Dictionary<string, string>
            {
                ["worker.second"] = "two",
                ["worker.first"] = "one",
            },
        };
        ResourceManifest equivalent = TestManifestFactory.Create() with
        {
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.Worker",
                Project = "/home/agent/two/Example.Worker.csproj",
                AppHost = "/home/agent/two/bin/Example.Worker",
                Image = "example/worker@sha256:1234",
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["A"] = "one",
                ["B"] = "two",
            },
            Properties = new Dictionary<string, string>
            {
                ["worker.first"] = "one",
                ["worker.second"] = "two",
            },
        };
        ResourceManifest changed = equivalent with
        {
            Artifact = equivalent.Artifact with { Image = "example/worker@sha256:5678" },
        };
        ResourceManifest second = TestManifestFactory.Create("second");

        // Act
        string firstHash = ResourceManifestCanonicalizer.ComputeHash(first);
        string equivalentHash = ResourceManifestCanonicalizer.ComputeHash(equivalent);
        string changedHash = ResourceManifestCanonicalizer.ComputeHash(changed);
        string closure = ResourceManifestCanonicalizer.ComputeClosureHash([first, second]);
        string reversedClosure = ResourceManifestCanonicalizer.ComputeClosureHash([second, equivalent]);

        // Assert
        firstHash.ShouldBe(equivalentHash);
        firstHash.Length.ShouldBe(64);
        firstHash.ShouldBe(firstHash.ToLowerInvariant());
        changedHash.ShouldNotBe(firstHash);
        closure.ShouldBe(reversedClosure);
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("appa"),
                ["--gateway=fake", "--environment=Development", "--restart-orphans"])
            .UseGateway(new FakeGateway());
        IApplicationResourceDescriptor database = builder.AddResource(
            TestManifestFactory.Create("database"));
        IApplicationResourceDescriptor api = builder.AddResource(
            TestManifestFactory.Create("api"));
        api.DependsOn(database);

        return builder.Build().Model;
    }
}
