using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlValueObjectTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: anchor names preserve YAML nonspace Unicode characters")]
    [InlineData("anchor")]
    [InlineData("a:b")]
    [InlineData("a\u0085b")]
    [InlineData("a\u00A0b")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\U0001F600b")]
    public void TryCreate_ValidAnchor_PreservesSpelling(string candidate)
    {
        // Arrange
        var expected = candidate;

        // Act
        var created = YamlAnchorName.TryCreate(candidate, out var anchor);

        // Assert
        created.ShouldBeTrue();
        anchor.Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: invalid anchor names return false")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a,b")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public void TryCreate_InvalidAnchor_ReturnsFalse(string? candidate)
    {
        // Arrange
        var input = candidate;

        // Act
        var created = YamlAnchorName.TryCreate(input, out var anchor);

        // Assert
        created.ShouldBeFalse();
        anchor.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: anchor names refuse controls, BOM and unpaired surrogates")]
    [InlineData(0x0000)]
    [InlineData(0x001F)]
    [InlineData(0x007F)]
    [InlineData(0x0084)]
    [InlineData(0x0086)]
    [InlineData(0xFEFF)]
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void TryCreate_InvalidAnchorCharacter_ReturnsFalse(int character)
    {
        // Arrange
        var candidate = "a" + (char)character + "b";

        // Act
        var created = YamlAnchorName.TryCreate(candidate, out var anchor);

        // Assert
        created.ShouldBeFalse();
        anchor.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: resolved tags preserve URI characters and percent escapes")]
    [InlineData("!")]
    [InlineData("!local")]
    [InlineData("!$:?")]
    [InlineData("tag:yaml.org,2002:str")]
    [InlineData("tag:example.com,2026:a%5B%5D!b")]
    [InlineData("URN:example:Tag")]
    [InlineData("HTTP://EXAMPLE.COM:80/a/../b%2f?q=%6a#Tag")]
    [InlineData("x:")]
    [InlineData("x:tag")]
    [InlineData("x:/tag")]
    [InlineData("x://example.com/tag")]
    [InlineData("!%6a%2F")]
    [InlineData("tag:example.com,2026:%E2%82%AC")]
    public void TryCreate_ValidTag_PreservesSpelling(string candidate)
    {
        // Arrange
        var expected = candidate;

        // Act
        var created = YamlTag.TryCreate(candidate, out var tag);

        // Assert
        created.ShouldBeTrue();
        tag.Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: tags refuse invalid URI characters and incomplete percent escapes")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!a b")]
    [InlineData("!a\nb")]
    [InlineData("!a>b")]
    [InlineData("!a{b")]
    [InlineData("!\u03A9")]
    [InlineData("!a%")]
    [InlineData("!a%2")]
    [InlineData("!a%2G")]
    public void TryCreate_InvalidTag_ReturnsFalse(string? candidate)
    {
        // Arrange
        var input = candidate;

        // Act
        var created = YamlTag.TryCreate(input, out var tag);

        // Assert
        created.ShouldBeFalse();
        tag.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - TryCreate: global tags require well-formed absolute URIs")]
    [InlineData("$:?")]
    [InlineData("bare")]
    [InlineData("relative/tag")]
    [InlineData("//example.com/tag")]
    [InlineData("1tag:value")]
    [InlineData("http://")]
    [InlineData("http://[invalid]/tag")]
    [InlineData("tag:example.com,2026:a[]!b")]
    [InlineData("tag:example.com,2026:tag#one#two")]
    [InlineData("x:tag#one#two")]
    public void TryCreate_NonUriGlobalTag_ReturnsFalse(string candidate)
    {
        // Arrange
        var input = candidate;

        // Act
        var created = YamlTag.TryCreate(input, out var tag);

        // Assert
        created.ShouldBeFalse();
        tag.Value.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Constructor: global tags refuse invalid URIs")]
    [InlineData("$:?")]
    [InlineData("bare")]
    public void Constructor_NonUriGlobalTag_ThrowsArgumentException(string candidate)
    {
        // Arrange
        var input = candidate;

        // Act
        Action construct = () => _ = new YamlTag(input);

        // Assert
        Should.Throw<ArgumentException>(construct).ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - CompareTo: anchor equality and ordering are ordinal and case-sensitive")]
    public void CompareTo_AnchorCaseDifference_UsesOrdinalComparison()
    {
        // Arrange
        var upper = new YamlAnchorName("Anchor");
        var lower = new YamlAnchorName("anchor");
        var same = new YamlAnchorName("Anchor");

        // Act
        var comparison = upper.CompareTo(lower);

        // Assert
        comparison.ShouldBeLessThan(0);
        upper.Equals(lower).ShouldBeFalse();
        upper.Equals(same).ShouldBeTrue();
        upper.GetHashCode().ShouldBe(same.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - CompareTo: tags use ordinal spelling without decoding percent escapes")]
    public void CompareTo_TagCaseAndEscapes_UsesOrdinalComparison()
    {
        // Arrange
        var upper = new YamlTag("!Tag%2F");
        var lower = new YamlTag("!tag%2F");
        var lowerEscape = new YamlTag("!Tag%2f");
        var decoded = new YamlTag("!Tag/");
        var same = new YamlTag("!Tag%2F");

        // Act
        var comparison = upper.CompareTo(lower);

        // Assert
        comparison.ShouldBeLessThan(0);
        upper.Equals(lower).ShouldBeFalse();
        upper.Equals(lowerEscape).ShouldBeFalse();
        upper.Equals(decoded).ShouldBeFalse();
        upper.Equals(same).ShouldBeTrue();
        upper.GetHashCode().ShouldBe(same.GetHashCode());
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Convert: nullable anchor strings preserve presence and spelling in both directions")]
    [InlineData(null)]
    [InlineData("Anchor")]
    public void Convert_NullableAnchorString_RoundTrips(string? input)
    {
        // Arrange
        var expected = input;

        // Act
        YamlAnchorName? anchor = input;
        string? output = anchor;

        // Assert
        anchor.HasValue.ShouldBe(input is not null);
        output.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Convert: nullable tag strings preserve presence and spelling in both directions")]
    [InlineData(null)]
    [InlineData("!Tag%2F")]
    public void Convert_NullableTagString_RoundTrips(string? input)
    {
        // Arrange
        var expected = input;

        // Act
        YamlTag? tag = input;
        string? output = tag;

        // Assert
        tag.HasValue.ShouldBe(input is not null);
        output.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Anchor: wrapped default values are refused without replacing the existing name")]
    public void Anchor_WrappedDefault_ThrowsWithoutChangingName()
    {
        // Arrange
        var node = YamlScalar.FromString("value");
        node.Anchor = "existing";
        YamlAnchorName? uninitialized = default(YamlAnchorName);

        // Act
        Action assign = () => node.Anchor = uninitialized;

        // Assert
        Should.Throw<ArgumentException>(assign);
        (node.Anchor?.ToString()).ShouldBe("existing");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Tag: wrapped default values are refused without replacing the existing tag")]
    public void Tag_WrappedDefault_ThrowsWithoutChangingTag()
    {
        // Arrange
        var node = YamlScalar.FromString("value");
        node.Tag = "!existing";
        YamlTag? uninitialized = default(YamlTag);

        // Act
        Action assign = () => node.Tag = uninitialized;

        // Assert
        Should.Throw<ArgumentException>(assign);
        (node.Tag?.ToString()).ShouldBe("!existing");
    }
}
