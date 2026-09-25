using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

public sealed class SecretStoreResourceTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Constructor: wraps an immutable manifest snapshot with typed options")]
    public void Constructor_WithManifestAndOptions_ShouldExposeSnapshotAndTypedOptions()
    {
        // Arrange
        ResourceManifest manifest = SecretStoreManifestFactory.Create();
        var options = new SecretStoreResourceOptions { Replicas = 1 };
        options.Storage.Size = "20Gi";

        // Act
        var resource = new SecretStoreResource(manifest, options);

        // Assert
        resource.Manifest.ShouldNotBeSameAs(manifest);
        resource.Name.ShouldBe(manifest.Name);
        resource.Artifact.ShouldBe(manifest.Artifact.Assembly);
        resource.Endpoints.Count.ShouldBe(manifest.Endpoints.Count);
        resource.Mounts.Count.ShouldBe(manifest.Mounts.Count);
        resource.Options.ShouldBeSameAs(options);
        resource.PlannerName.ShouldBe("SecretStore planner");
        resource.Manifest.ControlPlane.Endpoint.ShouldBe("api");
        resource.Manifest.ControlPlane.Path.ShouldBe("/cohesion/v1");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AddSecretStore: composes the manifest with typed options")]
    public void AddSecretStore_WithManifestAndOptions_ShouldReturnTypedResourceDescriptor()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();
        ResourceManifest manifest = SecretStoreManifestFactory.Create();
        var expectedOptions = new SecretStoreResourceOptions { Replicas = 1 };
        expectedOptions.Storage.Size = "25Gi";

        // Act
        ISecretStoreResourceDescriptor descriptor = builder.AddSecretStore(
            manifest,
            expectedOptions);

        // Assert
        SecretStoreResource resource = descriptor.Resource;
        SecretStoreResourceOptions options = resource.Options
            .ShouldBeOfType<SecretStoreResourceOptions>();
        options.ShouldBeSameAs(expectedOptions);
        options.Replicas.ShouldBe(1);
        options.Storage.Size.ShouldBe("25Gi");
        descriptor.ShouldBeAssignableTo<IApplicationResourceDescriptor>();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AddSecretStore: rejects a null manifest")]
    public void AddSecretStore_WithNullManifest_ShouldThrow()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder();

        // Act
        ArgumentNullException error = Should.Throw<ArgumentNullException>(
            () => builder.AddSecretStore(null!));

        // Assert
        error.ParamName.ShouldBe("manifest");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CreatePlan: rejects a context for another resource")]
    public void CreatePlan_WithMismatchedContext_ShouldThrow()
    {
        // Arrange
        var resource = new SecretStoreResource(SecretStoreManifestFactory.Create());
        ResourceManifest otherManifest = SecretStoreManifestFactory.Create("other-secretstore");
        var context = new PlanContext(
            otherManifest,
            resource.Options,
            Application.CreateBuilder().Environment,
            new Dictionary<string, ResourceManifest>());

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => resource.CreatePlan(context));

        // Assert
        error.Message.ShouldContain("does not match", Case.Sensitive);
    }
}
