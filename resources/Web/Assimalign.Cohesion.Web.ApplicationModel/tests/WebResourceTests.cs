using System;

using Assimalign.Cohesion.ApplicationModel;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.ApplicationModel.Tests;

public sealed class WebResourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - Constructor: wraps an immutable manifest snapshot with typed options")]
    public void Constructor_WithManifestAndOptions_ShouldExposeSnapshotAndTypedOptions()
    {
        // Arrange
        ResourceManifest manifest = WebManifestFactory.Create();
        var options = new WebResourceOptions { Replicas = 3 };

        // Act
        var resource = new WebResource(manifest, options);

        // Assert
        resource.Manifest.ShouldNotBeSameAs(manifest);
        resource.Name.ShouldBe(manifest.Name);
        resource.Artifact.ShouldBe(manifest.Artifact.Assembly);
        resource.Endpoints.Count.ShouldBe(manifest.Endpoints.Count);
        resource.Mounts.ShouldBeEmpty();
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("Web planner");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - AddWeb: composes the manifest with typed options")]
    public void AddWeb_WithManifestAndOptions_ShouldReturnWebResourceDescriptor()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        ResourceManifest manifest = WebManifestFactory.Create();
        var options = new WebResourceOptions { Replicas = 3 };

        // Act
        IWebResourceDescriptor descriptor = builder.AddWeb(manifest, options);

        // Assert
        WebResource resource = descriptor.Resource.ShouldBeOfType<WebResource>();
        resource.Options.ShouldBeSameAs(options);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - AddWeb: rejects a null manifest")]
    public void AddWeb_WithNullManifest_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException error = Should.Throw<ArgumentNullException>(
            () => builder.AddWeb(null!));

        // Assert
        error.ParamName.ShouldBe("manifest");
    }

}
