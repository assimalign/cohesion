using System;
using System.Text.Json;
using System.Text.Json.Nodes;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationNameTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Parse refuses invalid application identities")]
    [InlineData("")]
    [InlineData("AppA")]
    [InlineData("-appa")]
    [InlineData("appa-")]
    [InlineData("app_a")]
    [InlineData("app\r\n")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl")]
    public void Parse_InvalidIdentity_Throws(string value)
    {
        // Arrange / Act
        Action parse = () => ApplicationName.Parse(value);

        // Assert
        Should.Throw<ArgumentException>(parse);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Implicit conversion refuses invalid application identities")]
    [InlineData("AppA")]
    [InlineData("app_a")]
    public void Convert_InvalidIdentity_Throws(string value)
    {
        // Arrange / Act
        Action convert = () => { ApplicationName name = value; };

        // Assert
        Should.Throw<ArgumentException>(convert);
    }
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Document import refuses invalid application identities")]
    public void ToModel_InvalidIdentity_Throws()
    {
        // Arrange
        var document = new ApplicationModelDocument(
            ApplicationModelDocument.CurrentSchema, "Bad_Name", "Local", "fake",
            "Bad_Name@fake", "Describe", false, false, []);

        // Act
        Action import = () => document.ToModel();

        // Assert
        Should.Throw<ArgumentException>(import);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - TryCreate accepts valid application label boundaries")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("app-1")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk")]
    public void TryCreate_ValidIdentity_PreservesText(string value)
    {
        // Arrange / Act
        bool created = ApplicationName.TryCreate(value, out ApplicationName name);

        // Assert
        created.ShouldBeTrue();
        name.Value.ShouldBe(value);
        ApplicationName.IsValid(value, out string? message).ShouldBeTrue();
        message.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - TryCreate refuses invalid identity without throwing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("naïve")]
    [InlineData("-app")]
    [InlineData("app-")]
    [InlineData("app.name")]
    public void TryCreate_InvalidIdentity_ReturnsFalse(string? value)
    {
        // Arrange / Act
        bool created = ApplicationName.TryCreate(value, out ApplicationName name);

        // Assert
        created.ShouldBeFalse();
        name.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Manifest JSON rejects invalid application identities")]
    public void Parse_ManifestWithInvalidIdentity_ThrowsJsonException()
    {
        // Arrange
        var manifest = new ResourceManifest
        {
            Name = "app-api",
            Kind = "Web",
            Application = "app",
            ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
            Artifact = new ResourceManifestArtifact { Assembly = "Example.Api" },
            Endpoints = [new ResourceManifestEndpoint
            {
                Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080
            }],
            ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
            Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment }
        };
        string json = JsonSerializer.Serialize(manifest, ResourceManifestJsonContext.Default.ResourceManifest);
        ResourceManifest.Parse(json);
        JsonNode document = JsonNode.Parse(json).ShouldNotBeNull();
        document["application"] = "Bad_Name";
        string invalid = document.ToJsonString();

        // Act
        Action parse = () => ResourceManifest.Parse(invalid);

        // Assert
        Should.Throw<JsonException>(parse).Message.ShouldContain("RFC1123");
    }
}
