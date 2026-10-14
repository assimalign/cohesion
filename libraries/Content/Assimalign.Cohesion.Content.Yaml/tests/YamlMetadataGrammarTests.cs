using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlMetadataGrammarTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - ParseEvents: malformed anchor and tag metadata retains positioned YAML errors")]
    [InlineData("&bad\u0001 value")]
    [InlineData("&bad\uFEFF value")]
    [InlineData("!<> value")]
    [InlineData("!<bad tag> value")]
    [InlineData("!bad%escape value")]
    [InlineData("!<tag:\u03A9> value")]
    public void ParseEvents_InvalidMetadata_ThrowsYamlException(string input)
    {
        // Arrange
        var text = input;

        // Act
        Action parse = () => YamlText.ParseEvents(text);

        // Assert
        var exception = Should.Throw<YamlException>(parse);
        exception.Line.ShouldBe(1);
        exception.Column.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Write: tags with shorthand delimiters use verbatim spelling and round-trip")]
    [InlineData("!local,name")]
    [InlineData("!local[name]")]
    [InlineData("!local!suffix")]
    [InlineData("tag:yaml.org,2002:custom,name")]
    [InlineData("tag:yaml.org,2002:custom[name]")]
    [InlineData("tag:yaml.org,2002:custom!suffix")]
    public void Write_ResolvedTagWithShorthandDelimiter_UsesVerbatimTag(string tag)
    {
        // Arrange
        var node = YamlScalar.FromString("payload");
        node.Tag = tag;

        // Act
        var text = YamlText.Write(new YamlDocument(node));
        var reparsed = (YamlScalar)YamlText.ParseDocument(text).Root!;

        // Assert
        text.ShouldBe($"!<{tag}> payload\n");
        (reparsed.Tag?.ToString()).ShouldBe(tag);
        reparsed.Value.ShouldBe("payload");
        YamlText.Write(new YamlDocument(reparsed)).ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Write: percent-escaped tags preserve escape case and encoded characters")]
    [InlineData("!%6a%2F")]
    [InlineData("tag:example.com,2026:%6a%2F")]
    public void Write_PercentEscapedTag_PreservesSpelling(string tag)
    {
        // Arrange
        var node = YamlScalar.FromString("payload");
        node.Tag = tag;

        // Act
        var text = YamlText.Write(new YamlDocument(node));
        var reparsed = (YamlScalar)YamlText.ParseDocument(text).Root!;

        // Assert
        text.ShouldContain("%6a%2F", Case.Sensitive);
        (reparsed.Tag?.ToString()).ShouldBe(tag);
        reparsed.Value.ShouldBe("payload");
    }
}
