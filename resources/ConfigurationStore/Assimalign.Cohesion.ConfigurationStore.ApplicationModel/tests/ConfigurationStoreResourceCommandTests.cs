using System;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

public sealed class ConfigurationStoreResourceCommandTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: typed descriptor emits landed kinds and canonical values")]
    public void SetValueAndRemoveValue_WithTypedDescriptor_ShouldRecordWireCommands()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IConfigurationStoreResourceDescriptor descriptor = builder.AddConfigurationStore(ConfigurationStoreManifestFactory.Create());
        using JsonDocument value = JsonDocument.Parse("\"enabled\"");

        // Act
        IConfigurationStoreResourceDescriptor returned = descriptor.SetValue("orders", "options", value.RootElement)
            .SetValue("orders", "title", "Orders").RemoveValue("orders", "obsolete", optional: true);

        // Assert
        returned.ShouldBeSameAs(descriptor);
        descriptor.Commands.Count.ShouldBe(3);
        descriptor.Commands[0].Kind.ShouldBe("configurationstore.set-value");
        descriptor.Commands[0].Key.ShouldBe("orders/options");
        descriptor.Commands[0].Target.ShouldBeSameAs(descriptor.Resource);
        Encoding.UTF8.GetString(descriptor.Commands[0].Payload.Span)
            .ShouldBe("{\"key\":\"options\",\"namespace\":\"orders\",\"value\":\"enabled\"}");
        Encoding.UTF8.GetString(descriptor.Commands[1].Payload.Span)
            .ShouldBe("{\"key\":\"title\",\"namespace\":\"orders\",\"value\":\"Orders\"}");
        descriptor.Commands[2].Kind.ShouldBe("configurationstore.remove-value");
        descriptor.Commands[2].Optional.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - SetValue: follows the landed string-or-null store contract")]
    public void SetValue_WithNullOrUnsupportedJson_ShouldPreserveNullAndRejectObject()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IConfigurationStoreResourceDescriptor descriptor = builder.AddConfigurationStore(ConfigurationStoreManifestFactory.Create());
        using JsonDocument unsupported = JsonDocument.Parse("{}");

        // Act
        descriptor.SetValue("orders", "unset", (string?)null);
        ArgumentException error = Should.Throw<ArgumentException>(() => descriptor.SetValue("orders", "object", unsupported.RootElement));

        // Assert
        Encoding.UTF8.GetString(descriptor.Commands[0].Payload.Span)
            .ShouldBe("{\"key\":\"unset\",\"namespace\":\"orders\",\"value\":null}");
        error.ParamName.ShouldBe("value");
        descriptor.Commands.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Remote reference: returns a typed descriptor for the same graph node")]
    public void RemoteReferenceConfigurationStore_WithManifest_ShouldRetainIdentity()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        ResourceManifest manifest = ConfigurationStoreManifestFactory.Create("remote", "provider");
        var declaration = new ExternalResourceDeclaration(manifest.Name, manifest.Application, ["api"], false, manifest);

        // Act
        IConfigurationStoreResourceDescriptor descriptor = builder.RemoteReferenceConfigurationStore(declaration,
            options => options.Endpoint("api", "https://provider.example"));
        descriptor.SetValue("orders", "title", "Orders");
        IApplicationResourceDescriptor untyped = builder.RemoteReference(declaration,
            options => options.Endpoint("api", "https://provider.example"));

        // Assert
        descriptor.Resource.ShouldBeSameAs(untyped.Resource);
        descriptor.Commands[0].Owner.ShouldBe((ApplicationName)"appa");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Commands: value keys cannot alias another namespace")]
    public void SetValueAndRemoveValue_WithSlashInKey_ShouldRejectAmbiguousOwnershipKey()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IConfigurationStoreResourceDescriptor descriptor = builder.AddConfigurationStore(ConfigurationStoreManifestFactory.Create());

        // Act
        descriptor.SetValue("app/orders", "title", "Orders");
        ArgumentException set = Should.Throw<ArgumentException>(() => descriptor.SetValue("app", "orders/title", "Other"));
        ArgumentException remove = Should.Throw<ArgumentException>(() => descriptor.RemoveValue("app", "orders/title"));

        // Assert
        set.ParamName.ShouldBe("key");
        remove.ParamName.ShouldBe("key");
        descriptor.Commands.Count.ShouldBe(1);
        descriptor.Commands[0].Key.ShouldBe("app/orders/title");
    }
}
