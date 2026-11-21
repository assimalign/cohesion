using System;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// How a group is stored: one record in <c>groups</c>, found by its identifier and by its name
/// in lowercase, with a payload whose layout is pinned here.
/// </summary>
public class IdentityGroupCodecTests
{
    private const string minimal = """{"v":1,"name":"orders-admins","displayName":"orders-admins"}""";

    // The identifier IdentityGroupId.FromName derives from "orders-admins".
    private const string ordersAdminsId = "0933ab2c-248b-50ff-919a-577b7ead6f89";

    private readonly IdentityGroupCodec _codec = IdentityGroupCodec.Instance;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should key a group's record by its identifier and index its name uniquely in lowercase")]
    public void Encode_WithAGroup_ShouldKeyByIdentifierAndIndexTheLowercaseName()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new() { Name = "Orders-Admins" });

        // Act
        IdentityHubRecord record = _codec.Encode(group);

        // Assert
        _codec.Set.Value.ShouldBe("groups");
        record.Key.Value.ShouldBe(ordersAdminsId);
        record.ExpiresAt.ShouldBeNull();
        record.Indexes.Count.ShouldBe(1);
        record.Indexes[0].Name.Value.ShouldBe("name");
        record.Indexes[0].Value.Value.ShouldBe("orders-admins");
        record.Indexes[0].IsUnique.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should write a group's pinned layout")]
    public void Encode_WithACompleteGroup_ShouldWriteThePinnedLayout()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new()
        {
            Name = "orders-admins",
            DisplayName = "Orders administrators",
            Description = "Everyone who may change an order.",
        });

        // Act
        IdentityHubRecord record = _codec.Encode(group);

        // Assert
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe(
            """{"v":1,"name":"orders-admins","displayName":"Orders administrators","description":"Everyone who may change an order."}""");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should leave out the description a group does not have")]
    public void Encode_WithAMinimalGroup_ShouldWriteOnlyWhatItHas()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new() { Name = "orders-admins" });

        // Act
        IdentityHubRecord record = _codec.Encode(group);

        // Assert
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe(minimal);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should read back every part of a group")]
    public void Decode_WithAnEncodedGroup_ShouldReadBackEveryMember()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new()
        {
            Id = IdentityGroupId.New(),
            Name = "Orders-Admins",
            DisplayName = "Órdenes ✓",
            Description = "Line one.\nLine two.",
        });

        // Act
        IdentityGroup decoded = _codec.Decode(_codec.Encode(group));

        // Assert
        decoded.Id.ShouldBe(group.Id);
        decoded.Name.Value.ShouldBe("Orders-Admins");
        decoded.DisplayName.ShouldBe("Órdenes ✓");
        decoded.Description.ShouldBe("Line one.\nLine two.");
        decoded.IsLocked.ShouldBeFalse();
        decoded.Version.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a member the group layout does not know")]
    [InlineData("""{"v":1,"name":"orders-admins","displayName":"orders-admins","members":[]}""")]
    public void Decode_WithAnUnknownMember_ShouldThrowJsonException(string payload)
    {
        // Act and Assert
        Should.Throw<JsonException>(() => _codec.Decode(Record(ordersAdminsId, payload)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a group layout version it does not read")]
    [InlineData("""{"v":0,"name":"orders-admins","displayName":"orders-admins"}""", "0")]
    [InlineData("""{"v":2,"name":"orders-admins","displayName":"orders-admins"}""", "2")]
    public void Decode_WithAnUnknownLayout_ShouldThrowFormatExceptionNamingIt(string payload, string version)
    {
        // Act
        FormatException exception = Should.Throw<FormatException>(() => _codec.Decode(Record(ordersAdminsId, payload)));

        // Assert
        exception.Message.ShouldContain($"version {version}", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a group payload that does not follow the layout")]
    [InlineData("null")]
    [InlineData("""{"v":1,"displayName":"orders-admins"}""")]
    [InlineData("""{"v":1,"name":"orders-admins"}""")]
    public void Decode_WithAPayloadThatBreaksTheLayout_ShouldThrowFormatException(string payload)
    {
        // Act
        FormatException exception = Should.Throw<FormatException>(() => _codec.Decode(Record(ordersAdminsId, payload)));

        // Assert
        exception.Message.ShouldContain("does not follow its layout", Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should check a stored group as it checks a new one")]
    [InlineData("""{"v":1,"name":"orders admins","displayName":"orders-admins"}""")]
    [InlineData("""{"v":1,"name":"orders-admins","displayName":"   "}""")]
    public void Decode_WithAStoredGroupThatBreaksARule_ShouldThrowArgumentException(string payload)
    {
        // Act and Assert
        Should.Throw<ArgumentException>(() => _codec.Decode(Record(ordersAdminsId, payload)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a record whose key is not the one spelling of a group identifier")]
    [InlineData("orders-admins")]
    [InlineData("0933AB2C-248B-50FF-919A-577B7EAD6F89")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Decode_WithAKeyThatIsNoGroupIdentifier_ShouldThrowFormatException(string key)
    {
        // Act and Assert
        Should.Throw<FormatException>(() => _codec.Decode(Record(key, minimal)));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode, Decode: Should refuse a null group and a null record")]
    public void EncodeAndDecode_WithNull_ShouldThrowArgumentNullException()
    {
        // Act and Assert
        Should.Throw<ArgumentNullException>(() => _codec.Encode(null!));
        Should.Throw<ArgumentNullException>(() => _codec.Decode(null!));
    }

    private static IdentityHubRecord Record(string key, string payload)
    {
        return new IdentityHubRecord(new IdentityHubRecordKey(key), Encoding.UTF8.GetBytes(payload));
    }
}
