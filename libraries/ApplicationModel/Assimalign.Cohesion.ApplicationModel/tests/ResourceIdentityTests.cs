using System;
using System.Security.Cryptography;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ResourceIdentityTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Resource identity: ";

    [Theory(DisplayName = DisplayPrefix + "Resource names are RFC 1035 labels")]
    [InlineData("a")]
    [InlineData("orders-api")]
    [InlineData("orders--db")]
    [InlineData("api2")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk")]
    public void ResourceName_TryCreate_WithLabel_ShouldSucceed(string value)
    {
        // Arrange & Act
        bool created = ResourceName.TryCreate(value, out ResourceName name);

        // Assert
        created.ShouldBeTrue();
        name.Value.ShouldBe(value);
    }

    [Theory(DisplayName = DisplayPrefix + "Resource names reject anything but RFC 1035 labels")]
    [InlineData("")]
    [InlineData("Orders")]
    [InlineData("1orders")]
    [InlineData("-orders")]
    [InlineData("orders-")]
    [InlineData("orders_api")]
    [InlineData("orders.api")]
    [InlineData("Orders API")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl")]
    public void ResourceName_TryCreate_WithNonLabel_ShouldFail(string value)
    {
        // Arrange & Act
        bool created = ResourceName.TryCreate(value, out _);

        // Assert
        created.ShouldBeFalse();
    }

    [Theory(DisplayName = DisplayPrefix + "Provider names are dot-separated segments")]
    [InlineData("Web")]
    [InlineData("Database.Sql")]
    [InlineData("MessageHub")]
    [InlineData("a.b2.C")]
    public void ProviderName_TryCreate_WithSegments_ShouldSucceed(string value)
    {
        // Arrange & Act
        bool created = ProviderName.TryCreate(value, out _);

        // Assert
        created.ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "Provider names reject empty or malformed segments")]
    [InlineData("")]
    [InlineData(".Web")]
    [InlineData("Web.")]
    [InlineData("Web..Sql")]
    [InlineData("1Web")]
    [InlineData("Web.1Sql")]
    [InlineData("Message-Hub")]
    public void ProviderName_TryCreate_WithMalformedSegment_ShouldFail(string value)
    {
        // Arrange & Act
        bool created = ProviderName.TryCreate(value, out _);

        // Assert
        created.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Provider names compare case-insensitively")]
    public void ProviderName_Equals_WithDifferentCase_ShouldBeEqual()
    {
        // Arrange
        ProviderName declared = "Database.Sql";
        ProviderName requested = "database.sql";

        // Act & Assert
        declared.ShouldBe(requested);
        declared.GetHashCode().ShouldBe(requested.GetHashCode());
    }

    [Theory(DisplayName = DisplayPrefix + "Platform names are hyphen-separated lowercase words")]
    [InlineData("inprocess")]
    [InlineData("kubernetes")]
    [InlineData("azure-container-apps")]
    public void PlatformName_TryCreate_WithWords_ShouldSucceed(string value)
    {
        // Arrange & Act
        bool created = PlatformName.TryCreate(value, out _);

        // Assert
        created.ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "Platform names reject empty words and other characters")]
    [InlineData("")]
    [InlineData("Kubernetes")]
    [InlineData("-k8s")]
    [InlineData("k8s-")]
    [InlineData("in--process")]
    [InlineData("in_process")]
    public void PlatformName_TryCreate_WithMalformedWord_ShouldFail(string value)
    {
        // Arrange & Act
        bool created = PlatformName.TryCreate(value, out _);

        // Assert
        created.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Provider ids ignore the case of the name")]
    public void ProviderId_FromName_WithDifferentCase_ShouldDeriveOneId()
    {
        // Arrange & Act
        ProviderId declared = ProviderId.FromName("Web");
        ProviderId requested = ProviderId.FromName("web");

        // Assert
        declared.ShouldBe(requested);
    }

    [Fact(DisplayName = DisplayPrefix + "Resource ids are scoped by provider")]
    public void ResourceId_FromName_WithSameNameUnderTwoProviders_ShouldDeriveTwoIds()
    {
        // Arrange & Act
        ResourceId web = ResourceId.FromName("Web", "orders");
        ResourceId database = ResourceId.FromName("Database.Sql", "orders");

        // Assert
        web.ShouldNotBe(database);
        ResourceId.FromName("WEB", "orders").ShouldBe(web);
    }

    [Fact(DisplayName = DisplayPrefix + "Ids are RFC 9562 version 5 UUIDs a gateway in any language can derive")]
    public void FromName_WithProviderAndName_ShouldMatchIndependentUuidVersion5()
    {
        // Arrange
        Guid resourceNamespace = new("e10d0c39-063e-43de-ac58-32a831744cbd");
        Guid providerNamespace = new("7a1afa1e-6690-4108-8c87-dc141c045fda");

        // Act
        Guid resourceId = ResourceId.FromName("Web", "orders-api").Value;
        Guid providerId = ProviderId.FromName("Web").Value;

        // Assert
        resourceId.ShouldBe(UuidVersion5(resourceNamespace, "web/orders-api"));
        providerId.ShouldBe(UuidVersion5(providerNamespace, "web"));
        resourceId.ShouldNotBe(UuidVersion5(providerNamespace, "web/orders-api"));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's id derives from its provider and name")]
    public void Id_OnResource_ShouldDeriveFromProviderAndName()
    {
        // Arrange
        IApplicationResource resource = new OrdersApi();

        // Act
        ResourceId id = resource.Id;

        // Assert
        id.ShouldBe(ResourceId.FromName(resource.Provider, resource.Name));
    }

    [Fact(DisplayName = DisplayPrefix + "Deriving an id refuses a default name")]
    public void FromName_WithDefaultName_ShouldThrow()
    {
        // Arrange, Act & Assert
        Should.Throw<ArgumentException>(() => ResourceId.FromName("Web", default));
        Should.Throw<ArgumentException>(() => ProviderId.FromName(default));
    }

    // RFC 9562 §5.5, written from the specification rather than from Core's implementation.
    private static Guid UuidVersion5(Guid namespaceId, string name)
    {
        byte[] input = [.. namespaceId.ToByteArray(bigEndian: true), .. Encoding.UTF8.GetBytes(name)];
        byte[] hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    // A resource with nothing but its identity, as every form of one has.
    private sealed class OrdersApi : IApplicationResource
    {
        public ProviderName Provider => "Web";

        public ResourceName Name => "orders-api";
    }
}
