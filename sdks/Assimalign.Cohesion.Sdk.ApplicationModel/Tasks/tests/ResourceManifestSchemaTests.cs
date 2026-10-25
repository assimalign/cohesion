using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class ResourceManifestSchemaTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk] - Resource schema accepts v2 management bindings")]
    [InlineData("cohesion/resource/v2")]
    public void Validate_WithSupportedVersion_ShouldAcceptContract(string version)
    {
        // Arrange
        JsonObject manifest = CreateManifest(version);

        // Act / Assert
        Should.NotThrow(() => Validate(manifest));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - Resource schema rejects the retired v1 contract")]
    public void Validate_WithRetiredVersion_ShouldRejectContract()
    {
        // Arrange
        JsonObject manifest = CreateManifest("cohesion/resource/v1");

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => Validate(manifest));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - Resource v2 schema rejects the retired commands catalog")]
    public void Validate_WithCommands_ShouldRejectContract()
    {
        // Arrange
        JsonObject manifest = CreateManifest("cohesion/resource/v2");
        manifest["commands"] = new JsonArray();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => Validate(manifest));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk] - Resource schema preserves opaque producer runtime metadata")]
    public void Validate_WithOpaqueRuntime_ShouldAcceptContract()
    {
        // Arrange
        JsonObject manifest = CreateManifest("cohesion/resource/v2");
        manifest["runtime"] = JsonNode.Parse("""
            { "states": { "custom": true }, "initialState": "producer-owned", "extension": [1, 2] }
            """);

        // Act / Assert
        Should.NotThrow(() => Validate(manifest));
    }

    private static void Validate(JsonObject manifest)
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(ConsumerWorkspace.ResourceSchemaPath));
        using JsonDocument document = JsonDocument.Parse(manifest.ToJsonString());
        FocusedJsonSchemaValidator.Validate(document.RootElement, schema.RootElement);
    }

    private static JsonObject CreateManifest(string version)
    {
        JsonObject manifest = JsonNode.Parse("""
            {
              "schema": "cohesion/resource/v2", "name": "web", "kind": "Web", "application": "sample",
              "applicationModel": "Example.Web", "artifact": {"assembly":"Example.Web","composable":true,"project":null,"apphost":null,"image":null},
              "endpoints":[{"name":"http","scheme":"http","protocol":"tcp","containerPort":8080,"public":false}],
              "probes":{}, "controlPlane":{"endpoint":"http","path":"/cohesion/v1"},
              "mounts":[], "settings":[], "references":[], "environment":{},
              "lifecycle":{"workload":"Deployment","replicas":1,"maxReplicas":null,"stopGraceSeconds":30,"restartPolicy":"OnFailure","exitCodes":"cohesion/sysexits/v1"},
              "properties":{},
              "runtime":{"schema":"cohesion/resource-runtime/v1","management":{"endpoint":"http","path":"/cohesion/v1","protocol":"cohesion/resource-state/v1"}}
            }
            """)!.AsObject();
        manifest["schema"] = version;
        return manifest;
    }
}
