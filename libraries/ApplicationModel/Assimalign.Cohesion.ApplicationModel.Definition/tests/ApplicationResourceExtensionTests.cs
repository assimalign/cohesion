using System;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationResourceExtensionTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResourceExtension: ";

    [Theory(DisplayName = DisplayPrefix + "Platform and extension names are lowercase words joined by hyphens")]
    [InlineData("kubernetes", true)]
    [InlineData("in-process", true)]
    [InlineData("k8s", true)]
    [InlineData("Kubernetes", false)]
    [InlineData("in--process", false)]
    [InlineData("-docker", false)]
    [InlineData("docker.compose", false)]
    [InlineData("", false)]
    public void Names_TryCreate_ShouldFollowTheirGrammar(string value, bool expected)
    {
        // Act & Assert
        PlatformName.TryCreate(value, out _).ShouldBe(expected);
        ExtensionName.TryCreate(value, out _).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Extensions are kept per platform, and declaring a platform and name again replaces the value in place")]
    public void WithExtension_ShouldKeepOnePerPlatformAndName()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

        // Act
        api.WithExtension("kubernetes", "autoscaler", Json("""{"maxReplicas":5}"""))
            .WithExtension("docker", "autoscaler", Json("""{"replicas":2}"""))
            .WithExtension("kubernetes", "autoscaler", Json("""{"maxReplicas":10}"""));

        // Assert
        root.Build().Model.Manifest.Resources.Single().Extensions
            .Select(extension => $"{extension.Platform}/{extension.Name}={extension.Value.GetRawText()}")
            .ShouldBe(["kubernetes/autoscaler={\"maxReplicas\":10}", "docker/autoscaler={\"replicas\":2}"]);
    }

    [Fact(DisplayName = DisplayPrefix + "An extension keeps its value after the document it came from is disposed")]
    public void WithExtension_ThenDisposeDocument_ShouldKeepTheValue()
    {
        // Arrange
        IApplicationResourceDefinition api = Application.CreateBuilder("root", []).AddResource("Web", "orders-api");

        // Act
        using (JsonDocument document = JsonDocument.Parse("""{"maxReplicas":5}"""))
        {
            api.WithExtension("kubernetes", "autoscaler", document.RootElement);
        }

        // Assert
        api.Extensions.Single().Value.GetProperty("maxReplicas").GetInt32().ShouldBe(5);
    }

    [Fact(DisplayName = DisplayPrefix + "An extension needs a platform, a name, and a JSON value, and cannot change once the application is built")]
    public void WithExtension_WithMissingPartOrAfterBuild_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");
        JsonElement value = Json("{}");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.WithExtension(default, "autoscaler", value));
        Should.Throw<ArgumentException>(() => api.WithExtension("kubernetes", default, value));
        Should.Throw<ArgumentException>(() => api.WithExtension("kubernetes", "autoscaler", default));
        root.Build();
        Should.Throw<InvalidOperationException>(() => api.WithExtension("kubernetes", "autoscaler", value));
        api.Extensions.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A manifest extension declared twice for one platform, or without a value, is refused")]
    public void Create_WithInvalidExtensions_ShouldThrow()
    {
        // Arrange
        var autoscaler = new ApplicationResourceExtension("kubernetes", "autoscaler", Json("""{"maxReplicas":5}"""));

        ApplicationManifest With(params ApplicationResourceExtension[] extensions)
        {
            return new ApplicationManifest("root", [new("Web", "orders-api", [], [], extensions)], []);
        }

        // Act & Assert
        Should.NotThrow(() => ApplicationModel.Create(With(autoscaler, autoscaler with { Platform = "docker" })));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(autoscaler, autoscaler with { Value = Json("{}") })));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(autoscaler with { Value = default })));
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
