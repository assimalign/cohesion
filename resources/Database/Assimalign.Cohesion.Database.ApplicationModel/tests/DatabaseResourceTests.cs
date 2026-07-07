using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

public class DatabaseResourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - ApplicationModel: Resource declares artifact, endpoint, and data mount")]
    public void Resource_Defaults_ShouldDeclareArtifactEndpointAndMount()
    {
        // Arrange
        var resource = new DatabaseResource("orders-db");

        // Assert
        resource.Artifact.ShouldBe("Assimalign.Cohesion.Database.Application");
        var endpoint = resource.Endpoints.ShouldHaveSingleItem();
        endpoint.Name.ShouldBe(DatabaseResource.EndpointName);
        endpoint.Scheme.ShouldBe(DatabaseResource.EndpointScheme);
        endpoint.Port.ShouldBe(0);
        endpoint.IsPublic.ShouldBeFalse();
        var mount = resource.Mounts.ShouldHaveSingleItem();
        mount.Kind.ShouldBe(ResourceMountKind.Volume);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - ApplicationModel: AddDatabase composes into the application graph")]
    public void AddDatabase_OnBuilder_ShouldReturnDescriptorWrappingResource()
    {
        // Arrange
        var builder = Application.CreateBuilder();

        // Act
        var descriptor = builder.AddDatabase("orders-db", options => options.Port = 6543);

        // Assert
        descriptor.ShouldNotBeNull();
        var resource = descriptor.Resource.ShouldBeOfType<DatabaseResource>();
        resource.Endpoints.Single().Port.ShouldBe(6543);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - ApplicationModel: Empty resource name is rejected")]
    public void Resource_EmptyName_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new DatabaseResource(" "));
    }
}
