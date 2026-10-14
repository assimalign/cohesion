using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public sealed class GeneratedValueTypeUlidJsonTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated ULID JSON: ";

    [Theory(DisplayName = displayPrefix + "malformed and overflowing scalar values throw JsonException")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("0000000000000000000000000!")]
    [InlineData("0000000000000000000000000I")]
    [InlineData("0000000000000000000000000L")]
    [InlineData("0000000000000000000000000O")]
    [InlineData("0000000000000000000000000U")]
    [InlineData("0000000000000000000000000i")]
    [InlineData("0000000000000000000000000l")]
    [InlineData("0000000000000000000000000o")]
    [InlineData("0000000000000000000000000u")]
    [InlineData("80000000000000000000000000")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void Json_InvalidUlidScalar_ShouldThrowJsonException(string input)
    {
        // Arrange
        input.Length.ShouldBe(26);
        string json = "\"" + input + "\"";

        // Act
        Action deserialize = () => JsonSerializer.Deserialize(json, GeneratedValueTypeJsonContext.Default.GeneratedUlid);

        // Assert
        Should.Throw<JsonException>(deserialize);
    }

    [Theory(DisplayName = displayPrefix + "malformed and overflowing keys cannot overwrite canonical keys")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("0000000000000000000000000!")]
    [InlineData("0000000000000000000000000I")]
    [InlineData("0000000000000000000000000L")]
    [InlineData("0000000000000000000000000O")]
    [InlineData("0000000000000000000000000U")]
    [InlineData("0000000000000000000000000i")]
    [InlineData("0000000000000000000000000l")]
    [InlineData("0000000000000000000000000o")]
    [InlineData("0000000000000000000000000u")]
    [InlineData("80000000000000000000000000")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void Json_InvalidUlidDictionaryKey_ShouldThrowJsonException(string input)
    {
        // Arrange
        input.Length.ShouldBe(26);
        string json = "{\"7ZZZZZZZZZZZZZZZZZZZZZZZZZ\":\"maximum\",\"00000000000000000000000000\":\"minimum\",\"" + input + "\":\"invalid\"}";

        // Act
        Action deserialize = () => JsonSerializer.Deserialize(json, GeneratedValueTypeJsonContext.Default.UlidDictionary);

        // Assert
        Should.Throw<JsonException>(deserialize);
    }

    [Theory(DisplayName = displayPrefix + "valid scalar and dictionary values preserve the ULID alphabet and bounds")]
    [InlineData("00000000000000000000000000")]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01arz3ndektsv4rrffq69g5fav")]
    public void Json_ValidUlidText_ShouldRoundTrip(string input)
    {
        // Arrange
        var context = GeneratedValueTypeJsonContext.Default;
        var expected = new GeneratedUlid(Ulid.Parse(input));

        // Act
        GeneratedUlid scalar = JsonSerializer.Deserialize("\"" + input + "\"", context.GeneratedUlid);
        var dictionary = JsonSerializer.Deserialize("{\"" + input + "\":\"value\"}", context.UlidDictionary);

        // Assert
        scalar.ShouldBe(expected);
        dictionary.ShouldNotBeNull().Count.ShouldBe(1);
        dictionary[expected].ShouldBe("value");
        string canonical = input.ToUpperInvariant();
        JsonSerializer.Serialize(scalar, context.GeneratedUlid).ShouldBe("\"" + canonical + "\"");
        JsonSerializer.Serialize(dictionary, context.UlidDictionary).ShouldBe("{\"" + canonical + "\":\"value\"}");
    }
}
