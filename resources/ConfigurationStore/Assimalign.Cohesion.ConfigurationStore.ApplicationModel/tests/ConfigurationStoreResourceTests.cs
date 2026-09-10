using System;

using Assimalign.Cohesion.ApplicationModel;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

public sealed class ConfigurationStoreResourceTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Constructor: wraps an immutable manifest snapshot with typed options")]
    public void Constructor_WithManifestAndOptions_ShouldExposeSnapshotAndTypedOptions()
    {
        // Arrange
        ResourceManifest manifest = ConfigurationStoreManifestFactory.Create();
        var options = new ConfigurationStoreResourceOptions { Replicas = 2 };
        options.Storage.Size = "20Gi";

        // Act
        var resource = new ConfigurationStoreResource(manifest, options);

        // Assert
        resource.Manifest.ShouldNotBeSameAs(manifest);
        resource.Name.ShouldBe(manifest.Name);
        resource.Artifact.ShouldBe(manifest.Artifact.Assembly);
        resource.Endpoints.Count.ShouldBe(manifest.Endpoints.Count);
        resource.Mounts.Count.ShouldBe(manifest.Mounts.Count);
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("ConfigurationStore planner");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - AddConfigurationStore: composes the manifest with typed options")]
    public void AddConfigurationStore_WithManifestAndOptions_ShouldReturnTypedResourceDescriptor()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        ResourceManifest manifest = ConfigurationStoreManifestFactory.Create();
        var expectedOptions = new ConfigurationStoreResourceOptions { Replicas = 2 };
        expectedOptions.Storage.Size = "25Gi";

        // Act
        IApplicationResourceDescriptor descriptor = builder.AddConfigurationStore(
            manifest,
            expectedOptions);

        // Assert
        ConfigurationStoreResource resource = descriptor.Resource
            .ShouldBeOfType<ConfigurationStoreResource>();
        ConfigurationStoreResourceOptions options = resource.Options
            .ShouldBeOfType<ConfigurationStoreResourceOptions>();
        options.ShouldBeSameAs(expectedOptions);
        options.Replicas.ShouldBe(2);
        options.Storage.Size.ShouldBe("25Gi");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - AddConfigurationStore: rejects a null manifest")]
    public void AddConfigurationStore_WithNullManifest_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException error = Should.Throw<ArgumentNullException>(
            () => builder.AddConfigurationStore(null!));

        // Assert
        error.ParamName.ShouldBe("manifest");
    }
}
