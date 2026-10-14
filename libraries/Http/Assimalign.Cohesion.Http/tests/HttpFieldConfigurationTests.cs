using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFieldConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - FieldName: Refuse malformed configured names")]
    [InlineData("")]
    [InlineData(":path")]
    [InlineData("bad name")]
    [InlineData("bad\tname")]
    [InlineData("bad:name")]
    [InlineData("bad\r\nname")]
    [InlineData("caf\u00e9")]
    public void TryCreate_InvalidName_ShouldReject(string value)
    {
        // Arrange / Act
        bool accepted = HttpFieldName.TryCreate(value, out HttpFieldName result);

        // Assert
        accepted.ShouldBeFalse();
        result.ShouldBe(default);
        Should.Throw<ArgumentException>(() => new HttpFieldName(value));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - FieldName: Case-insensitive identity preserves spelling")]
    public void Equals_DifferentCase_ShouldPreserveSpellingAndIdentity()
    {
        // Arrange
        HttpFieldName first = "X-Custom";
        HttpFieldName second = "x-custom";

        // Act / Assert
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.CompareTo(second).ShouldBe(0);
        first.Value.ShouldBe("X-Custom");
        HttpFieldName.TryCreate("!#$%&'*+-.^_`|~09AZaz", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - FieldValue: Refuse RFC CTLs except internal HTAB")]
    public void TryCreate_RfcControlCharacters_ShouldRejectExceptInternalTab()
    {
        // Arrange
        for (int code = 0; code <= 0x7f; code++)
        {
            char character = (char)code;
            if (code > 0x1f && code != 0x7f)
            {
                continue;
            }

            // Act
            bool accepted = HttpFieldValue.TryCreate($"a{character}b", out _);

            // Assert
            accepted.ShouldBe(character == '\t', $"control U+{code:X4}");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FieldValue: Preserve every C1 obs-text character in any position")]
    [InlineData("", "")]
    [InlineData("", "value")]
    [InlineData("value", "")]
    [InlineData("value", "value")]
    public void Create_C1ObsText_ShouldPreserveContent(string prefix, string suffix)
    {
        // Arrange — RFC 9110 §5.5 includes %x80-9F in obs-text, even at a value's boundaries.
        for (int code = 0x80; code <= 0x9f; code++)
        {
            string text = $"{prefix}{(char)code}{suffix}";

            // Act
            bool accepted = HttpFieldValue.TryCreate(text, out HttpFieldValue value);

            // Assert
            accepted.ShouldBeTrue($"obs-text U+{code:X4}");
            value.Value.ShouldBe(text);
            new HttpFieldValue(text).Value.ShouldBe(text);
            HttpFieldValue.IsValid(text, out string? message).ShouldBeTrue();
            message.ShouldBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FieldValue: Refuse boundary whitespace")]
    [InlineData(" value")]
    [InlineData("value ")]
    [InlineData("\tvalue")]
    [InlineData("value\t")]
    public void TryCreate_BoundaryWhitespace_ShouldReject(string value)
    {
        // Act / Assert
        HttpFieldValue.TryCreate(value, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new HttpFieldValue(value));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - FieldValue: Preserve case, empty content and optional absence")]
    public void Create_ValidContentAndNull_ShouldPreserveMeaning()
    {
        // Arrange
        HttpFieldValue upper = "Text/Plain";
        HttpFieldValue lower = "text/plain";
        string? absent = null;

        // Act
        HttpFieldName? name = absent;
        HttpFieldValue? value = absent;

        // Assert
        upper.ShouldNotBe(lower);
        upper.Value.ShouldBe("Text/Plain");
        new HttpFieldValue("").Value.ShouldBe("");
        HttpFieldValue.TryCreate("a \tb\u00e9", out _).ShouldBeTrue();
        name.ShouldBeNull();
        value.ShouldBeNull();
        HttpFieldName.TryCreate(null, out _).ShouldBeFalse();
        HttpFieldValue.TryCreate(null, out _).ShouldBeFalse();
        default(HttpFieldName).Value.ShouldBeNull();
        default(HttpFieldValue).Value.ShouldBeNull();
    }
}
