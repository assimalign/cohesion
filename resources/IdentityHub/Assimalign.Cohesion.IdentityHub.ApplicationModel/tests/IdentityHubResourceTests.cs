using System;

using Assimalign.Cohesion.ApplicationModel;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

public sealed class IdentityHubResourceTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - AddIdentityHub: composes the manifest with typed options")]
    public void AddIdentityHub_WithManifestAndOptions_ShouldReturnTypedResourceDescriptor()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        ResourceManifest manifest = IdentityHubManifestFactory.Create();
        var options = new IdentityHubResourceOptions();
        options.Storage.Size = "20Gi";

        // Act
        IApplicationResourceDescriptor descriptor = builder.AddIdentityHub(manifest, options);

        // Assert
        IdentityHubResource resource = descriptor.Resource.ShouldBeOfType<IdentityHubResource>();
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("IdentityHub planner");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - AddIdentityHub: rejects a null manifest")]
    public void AddIdentityHub_WithNullManifest_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException error = Should.Throw<ArgumentNullException>(
            () => builder.AddIdentityHub(null!));

        // Assert
        error.ParamName.ShouldBe("manifest");
    }
}
