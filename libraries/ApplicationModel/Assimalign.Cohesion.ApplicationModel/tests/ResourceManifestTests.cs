using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ResourceManifestTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Should load and validate the full resource-manifest wire shape")]
    public void Parse_WithFullWireShape_ShouldLoadAndValidateManifest()
    {
        const string json = """
            {
              "schema": "cohesion/resource/v2",
              "name": "appa-api",
              "kind": "Web",
              "application": "appa",
              "applicationModel": "Assimalign.Cohesion.Web.ApplicationModel",
              "artifact": {
                "assembly": "Example.AppA.Api",
                "composable": true,
                "project": "Example.AppA.Api.csproj",
                "apphost": "Example.AppA.Api.exe",
                "image": "example/appa-api@sha256:0123"
              },
              "endpoints": [
                {
                  "name": "https",
                  "scheme": "https",
                  "protocol": "tcp",
                  "containerPort": 8443,
                  "devPort": 58443,
                  "public": true,
                  "certificate": "tls"
                }
              ],
              "probes": {
                "readiness": { "endpoint": "https", "http": "/readyz" },
                "liveness": { "endpoint": "https", "http": "/livez" },
                "startup": { "endpoint": "https", "tcp": true }
              },
              "controlPlane": { "endpoint": "https", "path": "/cohesion/v1" },
              "mounts": [
                {
                  "name": "data",
                  "kind": "Volume",
                  "containerPath": "/cohesion/data",
                  "size": "10Gi"
                },
                {
                  "name": "tls",
                  "kind": "Secret",
                  "containerPath": "/cohesion/tls",
                  "source": "appa-secretstore:certs/appa-api"
                }
              ],
              "settings": [
                { "key": "Orders:PageSize", "default": "50", "type": "int" }
              ],
              "references": [
                {
                  "resource": "appa-database",
                  "application": "appa",
                  "endpoints": ["db"],
                  "optional": false,
                  "manifest": "Example.AppA.Database"
                }
              ],
              "environment": { "EXAMPLE_MODE": "test" },
              "lifecycle": {
                "workload": "Deployment",
                "replicas": 2,
                "maxReplicas": 4,
                "stopGraceSeconds": 30,
                "restartPolicy": "OnFailure",
                "exitCodes": "cohesion/sysexits/v1"
              },
              "properties": { "web.kind": "Api" }
            }
            """;

        ResourceManifest manifest = ResourceManifest.Parse(json);

        // The public manifest carries checked values while its JSON remains plain strings/nulls.
        ((object)manifest.ControlPlane.Path).ShouldBeOfType<ResourceHttpPath>();
        ((object?)manifest.Probes.Readiness!.Http).ShouldBeOfType<ResourceHttpPath>();
        ((object?)manifest.Mounts[1].Source).ShouldBeOfType<ResourceMountSource>();
        string rendered = JsonSerializer.Serialize(manifest, ResourceManifestJsonContext.Default.ResourceManifest);
        using JsonDocument wire = JsonDocument.Parse(rendered);
        wire.RootElement.GetProperty("controlPlane").GetProperty("path").GetString().ShouldBe("/cohesion/v1");
        wire.RootElement.GetProperty("probes").GetProperty("readiness").GetProperty("http").GetString().ShouldBe("/readyz");
        wire.RootElement.GetProperty("mounts")[1].GetProperty("source").GetString().ShouldBe("appa-secretstore:certs/appa-api");
        wire.RootElement.GetProperty("mounts")[0].GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
        wire.RootElement.GetProperty("probes").GetProperty("startup").GetProperty("http").ValueKind.ShouldBe(JsonValueKind.Null);


        manifest.Name.ToString().ShouldBe("appa-api");
        manifest.Application.ToString().ShouldBe("appa");
        manifest.Artifact.AppHost.ShouldBe("Example.AppA.Api.exe");
        manifest.Endpoints[0].Protocol.ShouldBe("tcp");
        manifest.Endpoints[0].Public.ShouldBeTrue();
        manifest.Probes.Startup!.Tcp.ShouldBe(true);
        manifest.ControlPlane.Path.Value.ShouldBe("/cohesion/v1");
        manifest.Mounts[0].Kind.ShouldBe(ResourceMountKind.Volume);
        manifest.Mounts[0].Size.ShouldBe("10Gi");
        manifest.Mounts[1].Kind.ShouldBe(ResourceMountKind.Secret);
        manifest.Mounts[1].Source!.Value.Value.ShouldBe("appa-secretstore:certs/appa-api");
        manifest.Settings[0].Type.ShouldBe("int");
        manifest.References[0].Endpoints.ShouldContain("db");
        manifest.EnvironmentVariables["EXAMPLE_MODE"].ShouldBe("test");
        manifest.Lifecycle.Workload.ShouldBe(WorkloadKind.Deployment);
        manifest.Properties["web.kind"].ShouldBe("Api");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Should reject a newer resource-manifest schema")]
    public void Parse_WithNewerSchema_ShouldThrowClearError()
    {
        const string json = """
            { "schema": "cohesion/resource/v3", "kind": "Web" }
            """;

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => ResourceManifest.Parse(json));

        exception.Message.ShouldContain("cohesion/resource/v3", Case.Sensitive);
        exception.Message.ShouldContain(ResourceManifest.SchemaV2, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Should require an explicit resource-manifest schema")]
    public void Parse_WithoutSchema_ShouldThrowJsonError()
    {
        const string json = """
            { "kind": "Web" }
            """;

        JsonException exception = Should.Throw<JsonException>(() => ResourceManifest.Parse(json));

        exception.Message.ShouldContain("schema", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Should reject unknown manifest fields")]
    public void Parse_WithUnknownField_ShouldThrowJsonError()
    {
        const string json = """
            {
              "schema": "cohesion/resource/v2",
              "name": "appa-api",
              "kind": "Web",
              "application": "appa",
              "applicationModel": "Assimalign.Cohesion.Web.ApplicationModel",
              "artifact": { "assembly": "Example.AppA.Api" },
              "endpoints": [
                { "name": "http", "scheme": "http", "protocol": "tcp", "containerPort": 8080 }
              ],
              "controlPlane": { "endpoint": "http", "path": "/cohesion/v1" },
              "lifecycle": { "workload": "Deployment" },
              "properties": { "web.kind": "Api" },
              "platformObject": "Deployment"
            }
            """;

        JsonException exception = Should.Throw<JsonException>(() => ResourceManifest.Parse(json));

        exception.Message.ShouldContain("platformObject", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - JSON context: Should round-trip manifests with string enums")]
    public void JsonContext_WithFullManifest_ShouldRoundTripStringEnums()
    {
        ResourceManifest expected = CreateManifest();

        string json = JsonSerializer.Serialize(
            expected,
            ResourceManifestJsonContext.Default.ResourceManifest);
        ResourceManifest? actual = JsonSerializer.Deserialize(
            json,
            ResourceManifestJsonContext.Default.ResourceManifest);

        actual.ShouldNotBeNull();
        actual.Validate();
        actual.Name.ShouldBe(expected.Name);
        actual.Application.ShouldBe(expected.Application);
        actual.Mounts[0].Kind.ShouldBe(ResourceMountKind.Volume);
        actual.Lifecycle.Workload.ShouldBe(WorkloadKind.StatefulSet);
        json.ShouldNotContain("\"commands\":", Case.Sensitive);
        json.ShouldContain("\"workload\":\"StatefulSet\"", Case.Sensitive);
        json.ShouldContain("\"kind\":\"Volume\"", Case.Sensitive);
        json.ShouldContain("\"environment\":", Case.Sensitive);
        json.ShouldNotContain("environmentVariables", Case.Sensitive);
        json.ShouldContain("\"image\":null", Case.Sensitive);
        json.ShouldContain("\"maxReplicas\":null", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject a property owned by another resource kind")]
    public void Validate_WithForeignPropertyPrefix_ShouldThrow()
    {
        ResourceManifest manifest = CreateManifest() with
        {
            Properties = new Dictionary<string, string>
            {
                ["database.kind"] = "Sql"
            }
        };

        InvalidDataException exception = Should.Throw<InvalidDataException>(() => manifest.Validate());

        exception.Message.ShouldContain("web.", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Should reject a null kind-property value")]
    public void Parse_WithNullPropertyValue_ShouldThrow()
    {
        ResourceManifest manifest = CreateManifest() with
        {
            Properties = new Dictionary<string, string>
            {
                ["web.kind"] = null!,
            },
        };
        string json = JsonSerializer.Serialize(
            manifest,
            ResourceManifestJsonContext.Default.ResourceManifest);

        InvalidDataException exception = Should.Throw<InvalidDataException>(
            () => ResourceManifest.Parse(json));

        exception.Message.ShouldContain("web.kind", Case.Sensitive);
        exception.Message.ShouldContain("null", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should reject a volume without a size")]
    public void Validate_WithUnsizedVolume_ShouldThrow()
    {
        ResourceManifest manifest = CreateManifest() with
        {
            Mounts = new[]
            {
                new ResourceManifestMount
                {
                    Name = "data",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/cohesion/data"
                }
            }
        };

        InvalidDataException exception = Should.Throw<InvalidDataException>(() => manifest.Validate());

        exception.Message.ShouldContain("data", Case.Sensitive);
        exception.Message.ShouldContain("size", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should require endpoint certificates to name Secret mounts")]
    [InlineData("missing", ResourceMountKind.Secret, "does not name a declared mount")]
    [InlineData("tls", ResourceMountKind.Configuration, "must be a Secret mount")]
    public void Validate_WithInvalidCertificateMount_ShouldThrow(
        string certificate,
        ResourceMountKind mountKind,
        string expectedMessage)
    {
        ResourceManifest baseline = CreateManifest();
        ResourceManifest manifest = baseline with
        {
            Endpoints =
            [
                baseline.Endpoints[0] with { Certificate = certificate },
            ],
            Mounts =
            [
                new ResourceManifestMount
                {
                    Name = "tls",
                    Kind = mountKind,
                    ContainerPath = "/cohesion/tls",
                    Source = "parameter:tls",
                },
            ],
        };

        InvalidDataException exception = Should.Throw<InvalidDataException>(() => manifest.Validate());

        exception.Message.ShouldContain(expectedMessage, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Accepts an HTTPS endpoint with a Secret certificate mount")]
    public void Validate_HttpsSecretCertificate_ShouldSucceed()
    {
        ResourceManifest baseline = CreateManifest();
        ResourceManifest manifest = baseline with
        {
            Endpoints = [baseline.Endpoints[0] with { Scheme = "https", Certificate = "tls" }],
            Mounts = [new ResourceManifestMount { Name = "tls", Kind = ResourceMountKind.Secret, ContainerPath = "/cohesion/mounts/tls" }],
        };
        Should.NotThrow(manifest.Validate);
        ResourceManifest.Parse(JsonSerializer.Serialize(manifest, ResourceManifestJsonContext.Default.ResourceManifest)).Endpoints[0].Certificate.ShouldBe("tls");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Validate: Should allow non-network probes without an endpoint")]
    public void Validate_WithExecAndDisabledProbesWithoutEndpoints_ShouldSucceed()
    {
        ResourceManifest manifest = CreateManifest() with
        {
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe { Exec = ["check-ready"] },
                Liveness = new ResourceManifestProbe { None = true },
            },
        };

        Should.NotThrow(() => manifest.Validate());
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource v2 omits command metadata and round-trips")]
    public void Parse_WithV2Manifest_ShouldOmitCommandsAndRoundTrip()
    {
        // Arrange
        ResourceManifest manifest = CreateManifest() with { Schema = ResourceManifest.SchemaV2 };

        // Act
        string json = JsonSerializer.Serialize(manifest, ResourceManifestJsonContext.Default.ResourceManifest);
        ResourceManifest copy = ResourceManifest.Parse(json);

        // Assert
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("commands", out _).ShouldBeFalse();
        document.RootElement.TryGetProperty("runtime", out _).ShouldBeFalse();
        copy.Schema.ShouldBe(ResourceManifest.SchemaV2);
        ResourceManifestCanonicalizer.ComputeHash(copy).ShouldBe(ResourceManifestCanonicalizer.ComputeHash(manifest));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource manifests reject the retired v1 schema")]
    public void Parse_WithRetiredV1_ShouldRefuseSchema()
    {
        // Arrange
        string json = JsonSerializer.Serialize(CreateManifest() with { Schema = "cohesion/resource/v1" },
            ResourceManifestJsonContext.Default.ResourceManifest);

        // Act / Assert
        Should.Throw<InvalidDataException>(() => ResourceManifest.Parse(json));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource manifests reject retired command metadata")]
    public void Parse_WithCommandMetadata_ShouldRefuseUnknownMember()
    {
        // Arrange
        string json = JsonSerializer.Serialize(CreateManifest(), ResourceManifestJsonContext.Default.ResourceManifest);
        json = json.Replace("\"schema\":", "\"commands\":[],\"schema\":", StringComparison.Ordinal);

        // Act / Assert
        Should.Throw<JsonException>(() => ResourceManifest.Parse(json)).Message.ShouldContain("commands", Case.Sensitive);
    }

    private static ResourceManifest CreateManifest()
        => new()
        {
            Schema = ResourceManifest.SchemaV2,
            Name = "appa-database",
            Kind = "Web",
            Application = "appa",
            ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = "Example.AppA.Database",
                Composable = true
            },
            Endpoints = new[]
            {
                new ResourceManifestEndpoint
                {
                    Name = "db",
                    Scheme = "tcp",
                    Protocol = "tcp",
                    ContainerPort = 5432
                }
            },
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe
                {
                    Endpoint = "db",
                    Tcp = true
                }
            },
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = "db",
                Path = "/cohesion/v1"
            },
            Mounts = new[]
            {
                new ResourceManifestMount
                {
                    Name = "data",
                    Kind = ResourceMountKind.Volume,
                    ContainerPath = "/cohesion/data",
                    Size = "10Gi"
                }
            },
            Settings = new[]
            {
                new ResourceManifestSetting
                {
                    Key = "Database:PoolSize",
                    Default = "20",
                    Type = "int"
                }
            },
            References = new[]
            {
                new ResourceManifestReference
                {
                    Resource = "appa-secretstore",
                    Application = "appa",
                    Endpoints = new[] { "api" },
                    Manifest = "Example.AppA.SecretStore"
                }
            },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["EXAMPLE_MODE"] = "test"
            },
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.StatefulSet,
                Replicas = 1,
                MaxReplicas = null
            },
            Properties = new Dictionary<string, string>
            {
                ["web.kind"] = "Api"
            }
        };
}
