using System;
using System.Security.Cryptography;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationIdentityTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Application identity: ";

    [Theory(DisplayName = DisplayPrefix + "Gateway names are RFC 1035 labels")]
    [InlineData("root", true)]
    [InlineData("east-2", true)]
    [InlineData("East", false)]
    [InlineData("2east", false)]
    [InlineData("east-", false)]
    [InlineData("east.us", false)]
    [InlineData("", false)]
    public void GatewayName_TryCreate_ShouldFollowLabelGrammar(string value, bool expected)
    {
        // Arrange & Act
        bool created = GatewayName.TryCreate(value, out _);

        // Assert
        created.ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "Permissions are dot-separated segments of hyphenated lowercase words")]
    [InlineData("data.read", true)]
    [InlineData("messages.send", true)]
    [InlineData("blob-containers.write", true)]
    [InlineData("admin", true)]
    [InlineData("Data.Read", false)]
    [InlineData("data..read", false)]
    [InlineData("data.-read", false)]
    [InlineData(".data", false)]
    [InlineData("data.", false)]
    [InlineData("data read", false)]
    [InlineData("", false)]
    public void Permission_TryCreate_ShouldFollowGrammar(string value, bool expected)
    {
        // Arrange & Act
        bool created = Permission.TryCreate(value, out _);

        // Assert
        created.ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Gateway ids are RFC 9562 version 5 UUIDs a gateway in any language can derive")]
    public void GatewayId_FromName_WithName_ShouldMatchIndependentUuidVersion5()
    {
        // Arrange
        Guid gatewayNamespace = new("205ec510-232d-4b57-8b6f-11bae6d7e919");

        // Act
        Guid id = GatewayId.FromName("east").Value;

        // Assert
        id.ShouldBe(UuidVersion5(gatewayNamespace, "east"));
    }

    [Fact(DisplayName = DisplayPrefix + "A set's id derives from its gateway's name")]
    public void Id_OnSet_ShouldDeriveFromName()
    {
        // Arrange
        IApplicationSet set = ApplicationModel.CreateBuilder("root");

        // Act & Assert
        set.Id.ShouldBe(GatewayId.FromName("root"));
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
}
