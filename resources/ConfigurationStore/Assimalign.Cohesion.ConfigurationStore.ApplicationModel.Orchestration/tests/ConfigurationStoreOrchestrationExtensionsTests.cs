using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

public sealed class ConfigurationStoreOrchestrationExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Registers the source provider under the store's name")]
    public void UseConfigurationStore_WithStoreResource_ShouldRegisterSourceUnderStoreName()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());

        // Act
        builder.UseConfigurationStore(store);

        // Assert
        builder.Providers.Sources.Count.ShouldBe(1);
        builder.Providers.Sources.ContainsKey(SourceRequestFactory.StoreName).ShouldBeTrue();
        IResourceSourceProvider provider = builder.Providers.Sources[SourceRequestFactory.StoreName]
            .ShouldBeOfType<ConfigurationStoreSourceProvider>();
        provider.ResourceKind.ShouldBe("ConfigurationStore");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Returns the same builder")]
    public void UseConfigurationStore_WithStoreResource_ShouldReturnSameBuilder()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());

        // Act
        IApplicationBuilder result = builder.UseConfigurationStore(store);

        // Assert
        result.ShouldBeSameAs(builder);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Registers no other provider role")]
    public void UseConfigurationStore_WithStoreResource_ShouldLeaveOtherProviderRolesEmpty()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());

        // Act
        builder.UseConfigurationStore(store);

        // Assert
        ApplicationProviders providers = builder.Providers;
        providers.CertificateAuthority.ShouldBeNull();
        providers.TrustStore.ShouldBeNull();
        providers.CommandInputs.ShouldBeEmpty();
        providers.Telemetry.ShouldBeNull();
        providers.CredentialIssuer.ShouldBeNull();
        providers.Callers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Repeating the call keeps the first registration")]
    public void UseConfigurationStore_CalledTwice_ShouldKeepFirstRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());
        builder.UseConfigurationStore(store);
        IResourceSourceProvider first = builder.Providers.Sources[SourceRequestFactory.StoreName];

        // Act
        IApplicationBuilder result = builder.UseConfigurationStore(store);

        // Assert
        result.ShouldBeSameAs(builder);
        builder.Providers.Sources.Count.ShouldBe(1);
        builder.Providers.Sources[SourceRequestFactory.StoreName].ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Never replaces another provider registered under the store's name")]
    public void UseConfigurationStore_WithOtherProviderRegistered_ShouldThrowAndKeepRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());
        var custom = new StubSourceProvider();
        builder.Providers.Sources[SourceRequestFactory.StoreName] = custom;

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.UseConfigurationStore(store));

        // Assert
        exception.Message.ShouldContain(
            $"Mount source '{SourceRequestFactory.StoreName}' is already registered with provider '{nameof(StubSourceProvider)}'",
            Case.Sensitive);
        builder.Providers.Sources.Count.ShouldBe(1);
        builder.Providers.Sources[SourceRequestFactory.StoreName].ShouldBeSameAs(custom);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Registers each store under its own name")]
    public void UseConfigurationStore_WithTwoStores_ShouldRegisterBoth()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor first = builder.AddResource(TestManifests.Store("settings-a"));
        IApplicationResourceDescriptor second = builder.AddResource(TestManifests.Store("settings-b"));

        // Act
        builder.UseConfigurationStore(first).UseConfigurationStore(second);

        // Assert
        builder.Providers.Sources.Keys.ShouldBe(["settings-a", "settings-b"], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Manifest kinds compare case-insensitively")]
    public void UseConfigurationStore_WithDifferentKindCase_ShouldRegister()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(
            TestManifests.Store(kind: "configurationstore"));

        // Act
        builder.UseConfigurationStore(store);

        // Assert
        builder.Providers.Sources.ContainsKey(SourceRequestFactory.StoreName).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Refuses a manifest resource of another kind")]
    public void UseConfigurationStore_WithOtherManifestKind_ShouldThrowAndRegisterNothing()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor secrets = builder.AddResource(TestManifests.Store("secrets", "SecretStore"));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.UseConfigurationStore(secrets));

        // Assert
        exception.ParamName.ShouldBe("store");
        exception.Message.ShouldContain("Resource 'secrets' is kind 'SecretStore'", Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: A resource without a manifest is registered for Build to check")]
    public void UseConfigurationStore_WithNonManifestResource_ShouldRegister()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(new NamedResource("custom-settings"));

        // Act
        builder.UseConfigurationStore(store);

        // Assert
        builder.Providers.Sources["custom-settings"].ShouldBeOfType<ConfigurationStoreSourceProvider>();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: A reserved source name is rejected")]
    public void UseConfigurationStore_WithReservedSourceName_ShouldThrowArgument()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store("parameter"));

        // Act
        Action use = () => builder.UseConfigurationStore(store);

        // Assert
        Should.Throw<ArgumentException>(use).Message.ShouldContain("is reserved", Case.Sensitive);
        builder.Providers.Sources.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Rejects a null store")]
    public void UseConfigurationStore_WithNullStore_ShouldThrowArgumentNull()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();

        // Act
        Action use = () => builder.UseConfigurationStore(null!);

        // Assert
        Should.Throw<ArgumentNullException>(use).ParamName.ShouldBe("store");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - UseConfigurationStore: Rejects a null builder")]
    public void UseConfigurationStore_WithNullBuilder_ShouldThrowArgumentNull()
    {
        // Arrange
        IApplicationResourceDescriptor store = CreateBuilder().AddResource(TestManifests.Store());

        // Act
        Action use = () => ConfigurationStoreOrchestrationExtensions.UseConfigurationStore(null!, store);

        // Assert
        Should.Throw<ArgumentNullException>(use).ParamName.ShouldBe("builder");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Build: The built model carries the registration as a frozen snapshot")]
    public void Build_AfterUseConfigurationStore_ShouldCarryFrozenRegistration()
    {
        // Arrange
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor store = builder.AddResource(TestManifests.Store());
        builder.AddResource(TestManifests.Consumer("api", $"{SourceRequestFactory.StoreName}:api"));
        builder.UseConfigurationStore(store);

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        IResourceSourceProvider provider = model.Providers.Sources[SourceRequestFactory.StoreName];
        provider.ShouldBeSameAs(builder.Providers.Sources[SourceRequestFactory.StoreName]);
        Should.Throw<NotSupportedException>(
            () => model.Providers.Sources["other"] = new ConfigurationStoreSourceProvider());
    }

    private static IApplicationBuilder CreateBuilder() => Application
        .CreateBuilder(ApplicationName.Parse("appa"), [])
        .UseGateway(new FakeGateway());
}
