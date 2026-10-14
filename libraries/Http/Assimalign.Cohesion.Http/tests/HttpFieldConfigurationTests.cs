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

    [Fact(DisplayName = "Cohesion Test [Http] - FieldValue: Refuse every control except internal HTAB")]
    public void TryCreate_ControlCharacters_ShouldRejectExceptInternalTab()
    {
        // Arrange
        for (int code = char.MinValue; code <= char.MaxValue; code++)
        {
            char character = (char)code;
            if (!char.IsControl(character))
            {
                continue;
            }

            // Act
            bool accepted = HttpFieldValue.TryCreate($"a{character}b", out _);

            // Assert
            accepted.ShouldBe(character == '\t', $"control U+{code:X4}");
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
