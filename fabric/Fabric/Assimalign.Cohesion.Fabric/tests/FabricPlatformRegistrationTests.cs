using System;

using Assimalign.Cohesion.Fabric.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.Tests;

public class FabricPlatformRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricPlatformRegistration: Should record what the platform registers")]
    public void Create_Platform_ShouldRecordRegistrations()
    {
        // Arrange
        var platform = new TestPlatform(builder => builder
            .AddFaultClassifier(new TestFaultClassifier())
            .AddExtension(new TestExtension("test", "Cohesion.Workloads/workloads"))
            .AddResourceType(new TestResourceProvider("Test.Core/manifests")));

        // Act
        var registration = FabricPlatformRegistration.Create(platform);

        // Assert
        registration.Name.ShouldBe(new PlatformName("test"));
        registration.FaultClassifiers.Count.ShouldBe(1);
        registration.Extensions.Count.ShouldBe(1);
        registration.Extensions[0].HubType.ShouldBe(typeof(TestHub));
        registration.ResourceProviders.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricPlatformRegistration: Should refuse a resource type registered twice")]
    public void Create_DuplicateResourceType_ShouldThrow()
    {
        // Arrange
        var platform = new TestPlatform(builder => builder
            .AddResourceType(new TestResourceProvider("Test.Core/manifests"))
            .AddResourceType(new TestResourceProvider("Test.Core/manifests")));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => FabricPlatformRegistration.Create(platform)).Message.ShouldContain("Test.Core/manifests");
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricPlatformRegistration: Should refuse another platform's extension")]
    public void Create_ForeignExtension_ShouldThrow()
    {
        // Arrange
        var platform = new TestPlatform(builder => builder.AddExtension(new TestExtension("other", "Cohesion.Workloads/workloads")));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => FabricPlatformRegistration.Create(platform));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricPlatformRegistration: Should refuse a registration after configuration")]
    public void Builder_AfterConfiguration_ShouldThrow()
    {
        // Arrange
        IFabricPlatformBuilder? kept = null;
        var platform = new TestPlatform(builder => kept = builder);
        FabricPlatformRegistration.Create(platform);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => kept!.AddFaultClassifier(new TestFaultClassifier()));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - FabricPlatformRegistration: Should refuse a platform without a name")]
    public void Create_DefaultName_ShouldThrow()
    {
        // Arrange
        var platform = new TestPlatform(_ => { }, name: null);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => FabricPlatformRegistration.Create(platform));
    }
}
