using System;
using System.Linq;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// How a user is stored: one record in <c>users</c>, found by its identifier and by its name in
/// lowercase, with a payload whose layout is pinned here.
/// </summary>
public class IdentityUserCodecTests
{
    private const string minimal = """{"v":1,"name":"ada","displayName":"ada","emailVerified":false,"enabled":true}""";

    // The identifier IdentityUserId.FromName derives from "ada".
    private const string adaId = "e2bffadd-19cb-5bce-817b-8a6008fd4d28";

    private readonly IdentityUserCodec _codec = IdentityUserCodec.Instance;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should key a user's record by its identifier and index its name uniquely in lowercase")]
    public void Encode_WithAUser_ShouldKeyByIdentifierAndIndexTheLowercaseName()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new() { Name = "Ada@Contoso.example" });

        // Act
        IdentityHubRecord record = _codec.Encode(user);

        // Assert
        _codec.Set.Value.ShouldBe("users");
        record.Key.Value.ShouldBe("47dc2387-b6b6-5006-b279-d4d23896d554");
        record.ExpiresAt.ShouldBeNull();
        record.Indexes.Count.ShouldBe(1);
        record.Indexes[0].Name.Value.ShouldBe("name");
        record.Indexes[0].Value.Value.ShouldBe("ada@contoso.example");
        record.Indexes[0].IsUnique.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should write a user's pinned layout")]
    public void Encode_WithACompleteUser_ShouldWriteThePinnedLayout()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new()
        {
            Name = "Ada",
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Email = "ada@contoso.example",
            IsEmailVerified = true,
            IsEnabled = false,
        });

        // Act
        IdentityHubRecord record = _codec.Encode(user);

        // Assert
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe(
            """{"v":1,"name":"Ada","displayName":"Ada Lovelace","givenName":"Ada","familyName":"Lovelace","email":"ada@contoso.example","emailVerified":true,"enabled":false}""");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should leave out what a user does not have")]
    public void Encode_WithAMinimalUser_ShouldWriteOnlyWhatItHas()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new() { Name = "ada" });

        // Act
        IdentityHubRecord record = _codec.Encode(user);

        // Assert
        Encoding.UTF8.GetString(record.Payload.Span).ShouldBe(minimal);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should read back every part of a user")]
    public void Decode_WithAnEncodedUser_ShouldReadBackEveryMember()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new()
        {
            Id = IdentityUserId.New(),
            Name = "Ada.Lovelace",
            DisplayName = "Ada Lovelace ✓",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Email = "ädä@contoso.example",
            IsEmailVerified = true,
            IsEnabled = false,
        });

        // Act
        IdentityUser decoded = _codec.Decode(_codec.Encode(user));

        // Assert
        decoded.Id.ShouldBe(user.Id);
        decoded.Name.Value.ShouldBe("Ada.Lovelace");
        decoded.DisplayName.ShouldBe("Ada Lovelace ✓");
        decoded.GivenName.ShouldBe("Ada");
        decoded.FamilyName.ShouldBe("Lovelace");
        decoded.Email.ShouldBe("ädä@contoso.example");
        decoded.IsEmailVerified.ShouldBeTrue();
        decoded.IsEnabled.ShouldBeFalse();
        decoded.IsLocked.ShouldBeFalse();
        decoded.Version.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should read the smallest payload as a user")]
    public void Decode_WithTheSmallestPayload_ShouldReadAUser()
    {
        // Act
        IdentityUser user = _codec.Decode(Record(adaId, minimal));

        // Assert
        user.Id.ShouldBe(IdentityUserId.FromName("ada"));
        user.Name.Value.ShouldBe("ada");
        user.DisplayName.ShouldBe("ada");
        user.Email.ShouldBeNull();
        user.IsEnabled.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a member the user layout does not know")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","emailVerified":false,"enabled":true,"password":"x"}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","emailVerified":false,"enabled":true,"Name":"ada"}""")]
    public void Decode_WithAnUnknownMember_ShouldThrowJsonException(string payload)
    {
        // Act and Assert
        Should.Throw<JsonException>(() => _codec.Decode(Record(adaId, payload)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a user layout version it does not read")]
    [InlineData("""{"v":0,"name":"ada","displayName":"ada","emailVerified":false,"enabled":true}""", "0")]
    [InlineData("""{"v":2,"name":"ada","displayName":"ada","emailVerified":false,"enabled":true}""", "2")]
    [InlineData("""{"name":"ada","displayName":"ada","emailVerified":false,"enabled":true}""", "0")]
    public void Decode_WithAnUnknownLayout_ShouldThrowFormatExceptionNamingIt(string payload, string version)
    {
        // Act
        FormatException exception = Should.Throw<FormatException>(() => _codec.Decode(Record(adaId, payload)));

        // Assert
        exception.Message.ShouldContain($"version {version}", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a user payload that does not follow the layout")]
    [InlineData("null")]
    [InlineData("""{"v":1,"displayName":"ada","emailVerified":false,"enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","emailVerified":false,"enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","emailVerified":false}""")]
    public void Decode_WithAPayloadThatBreaksTheLayout_ShouldThrowFormatException(string payload)
    {
        // Act
        FormatException exception = Should.Throw<FormatException>(() => _codec.Decode(Record(adaId, payload)));

        // Assert
        exception.Message.ShouldContain("does not follow its layout", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a user payload that is not JSON")]
    public void Decode_WithAPayloadThatIsNotJson_ShouldThrowJsonException()
    {
        // Act and Assert
        Should.Throw<JsonException>(() => _codec.Decode(Record(adaId, "not json")));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should check a stored user as it checks a new one")]
    [InlineData("""{"v":1,"name":"ada lovelace","displayName":"ada","emailVerified":false,"enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"   ","emailVerified":false,"enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","email":"nowhere","emailVerified":false,"enabled":true}""")]
    [InlineData("""{"v":1,"name":"ada","displayName":"ada","emailVerified":true,"enabled":true}""")]
    public void Decode_WithAStoredUserThatBreaksARule_ShouldThrowArgumentException(string payload)
    {
        // Act and Assert
        Should.Throw<ArgumentException>(() => _codec.Decode(Record(adaId, payload)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a record whose key is not the one spelling of a user identifier")]
    [InlineData("ada")]
    [InlineData("E2BFFADD-19CB-5BCE-817B-8A6008FD4D28")]
    [InlineData("e2bffadd19cb5bce817b8a6008fd4d28")]
    [InlineData("{e2bffadd-19cb-5bce-817b-8a6008fd4d28}")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Decode_WithAKeyThatIsNoUserIdentifier_ShouldThrowFormatException(string key)
    {
        // Act and Assert
        Should.Throw<FormatException>(() => _codec.Decode(Record(key, minimal)));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode, Decode: Should refuse a null user and a null record")]
    public void EncodeAndDecode_WithNull_ShouldThrowArgumentNullException()
    {
        // Act and Assert
        Should.Throw<ArgumentNullException>(() => _codec.Encode(null!));
        Should.Throw<ArgumentNullException>(() => _codec.Decode(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should fit a user at every limit inside one record")]
    public void Encode_WithAUserAtEveryLimit_ShouldFitOneRecord()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new()
        {
            Name = new string('n', IdentityUserName.MaxLength),
            DisplayName = new string('€', IdentityUser.MaxDisplayNameLength),
            GivenName = new string('€', IdentityUser.MaxPersonNameLength),
            FamilyName = new string('€', IdentityUser.MaxPersonNameLength),
            Email = new string('€', IdentityUser.MaxEmailLength - 2) + "@b",
            IsEmailVerified = true,
        });

        // Act
        IdentityHubRecord record = _codec.Encode(user);
        IdentityUser decoded = _codec.Decode(record);

        // Assert
        record.Payload.Length.ShouldBeLessThan(IdentityHubRecord.MaxPayloadLength);
        decoded.DisplayName.ShouldBe(user.DisplayName);
        decoded.Email.ShouldBe(user.Email);
    }

    private static IdentityHubRecord Record(string key, string payload)
    {
        return new IdentityHubRecord(new IdentityHubRecordKey(key), Encoding.UTF8.GetBytes(payload));
    }
}
