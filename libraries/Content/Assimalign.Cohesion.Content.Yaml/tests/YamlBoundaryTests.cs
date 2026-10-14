using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Anchor: invalid names are refused at assignment")]
    [InlineData("")]
    [InlineData("two names")]
    [InlineData("line\nbreak")]
    [InlineData("line\rbreak")]
    [InlineData("tab\tname")]
    [InlineData("name,other")]
    [InlineData("name[other")]
    [InlineData("name]other")]
    [InlineData("name{other")]
    [InlineData("name}other")]
    public void Anchor_InvalidName_ThrowsAtAssignment(string anchor)
    {
        // Arrange
        var node = YamlScalar.FromString("value");

        // Act
        Action assign = () => node.Anchor = anchor;

        // Assert
        Should.Throw<ArgumentException>(assign);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Tag: invalid tags are refused at assignment")]
    [InlineData("")]
    [InlineData("!two names")]
    [InlineData("!line\nbreak")]
    [InlineData("!line\rbreak")]
    [InlineData("!tab\tname")]
    [InlineData("tag:example.com,2026:unsafe>value")]
    public void Tag_InvalidTag_ThrowsAtAssignment(string tag)
    {
        // Arrange
        var node = YamlScalar.FromString("value");

        // Act
        Action assign = () => node.Tag = tag;

        // Assert
        Should.Throw<ArgumentException>(assign);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Tag: invalid global URIs are refused without replacing the existing tag")]
    [InlineData("$:?")]
    [InlineData("bare")]
    public void Tag_NonUriGlobalTag_ThrowsWithoutChangingTag(string tag)
    {
        // Arrange
        var node = YamlScalar.FromString("payload");
        node.Tag = "!existing";

        // Act
        Action assign = () => node.Tag = tag;

        // Assert
        Should.Throw<ArgumentException>(assign);
        (node.Tag?.ToString()).ShouldBe("!existing");
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - CreateWriter: non-positive indentation is refused before writing")]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateWriter_NonPositiveIndentSize_ThrowsAtCreation(int indentSize)
    {
        // Arrange
        var options = new YamlWriterOptions { IndentSize = indentSize };

        // Act
        Action create = () => YamlText.CreateWriter(options);

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(create).ParamName.ShouldBe(nameof(YamlWriterOptions.IndentSize));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Write: non-positive indentation is refused even for an empty stream")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Write_NonPositiveIndentSize_ThrowsBeforeEmission(int indentSize)
    {
        // Arrange
        var stream = new YamlStream();
        var options = new YamlWriterOptions { IndentSize = indentSize };

        // Act
        Action write = () => YamlText.Write(stream, options);

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(write).ParamName.ShouldBe(nameof(YamlWriterOptions.IndentSize));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Write: valid anchor names retain their spelling and shared node identity")]
    [InlineData("Shared-1")]
    [InlineData("a:b")]
    [InlineData("anchor!name")]
    [InlineData("\u03A9")]
    [InlineData("a\u0085b")]
    [InlineData("a\u00A0b")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\U0001F600b")]
    public void Write_ValidAnchor_RoundTripsSharedNode(string anchor)
    {
        // Arrange
        var shared = YamlScalar.FromString("value");
        shared.Anchor = anchor;
        var root = new YamlMapping { { "first", shared }, { "second", shared } };

        // Act
        var text = YamlText.Write(new YamlDocument(root));
        var reparsed = (YamlMapping)YamlText.ParseDocument(text).Root!;
        var events = YamlText.ParseEvents(text);

        // Assert
        text.ShouldBe($"first: &{anchor} value\nsecond: *{anchor}\n");
        (reparsed["first"].Anchor?.ToString()).ShouldBe(anchor);
        ReferenceEquals(reparsed["first"], reparsed["second"]).ShouldBeTrue();
        (events.Single(item => item.Kind == YamlEventKind.Scalar && item.Anchor is not null).Anchor?.ToString()).ShouldBe(anchor);
        events.Single(item => item.Kind == YamlEventKind.Alias).Value.ShouldBe(anchor);
        YamlText.Write(new YamlDocument(reparsed)).ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse and write: resolved local, global, core and named-handle tags round-trip")]
    [InlineData("!local payload", "!local", "!local payload\n", YamlScalarKind.String)]
    [InlineData("!<tag:example.com,2026:widget> payload", "tag:example.com,2026:widget", "!<tag:example.com,2026:widget> payload\n", YamlScalarKind.String)]
    [InlineData("!!str 42", "tag:yaml.org,2002:str", "!!str \"42\"\n", YamlScalarKind.String)]
    [InlineData("%TAG !e! tag:example.com,2026:\n---\n!e!widget payload", "tag:example.com,2026:widget", "--- !<tag:example.com,2026:widget> payload\n", YamlScalarKind.String)]
    public void Parse_ValidTags_RoundTripsResolvedTag(string input, string tag, string expectedText, YamlScalarKind kind)
    {
        // Arrange
        var document = YamlText.ParseDocument(input);

        // Act
        var text = YamlText.Write(document);
        var reparsed = (YamlScalar)YamlText.ParseDocument(text).Root!;
        var scalarEvent = YamlText.ParseEvents(input).Single(item => item.Kind == YamlEventKind.Scalar);

        // Assert
        (document.Root!.Tag?.ToString()).ShouldBe(tag);
        (scalarEvent.Tag?.ToString()).ShouldBe(tag);
        text.ShouldBe(expectedText);
        (reparsed.Tag?.ToString()).ShouldBe(tag);
        reparsed.Kind.ShouldBe(kind);
        reparsed.Value.ShouldBe(((YamlScalar)document.Root).Value);
        YamlText.Write(YamlText.ParseDocument(text)).ShouldBe(text);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse and write: non-specific tags preserve existing core-schema and emission behavior")]
    public void Parse_NonSpecificTag_PreservesExistingRoundTripSemantics()
    {
        // Arrange
        const string input = "! 42";

        // Act
        var scalar = (YamlScalar)YamlText.ParseDocument(input).Root!;
        var text = YamlText.Write(new YamlDocument(scalar));
        var reparsed = (YamlScalar)YamlText.ParseDocument(text).Root!;
        var scalarEvent = YamlText.ParseEvents(input).Single(item => item.Kind == YamlEventKind.Scalar);

        // Assert
        (scalar.Tag?.ToString()).ShouldBe("!");
        (scalarEvent.Tag?.ToString()).ShouldBe("!");
        scalar.Kind.ShouldBe(YamlScalarKind.Integer);
        text.ShouldBe("42\n");
        reparsed.Tag.ShouldBeNull();
        reparsed.Kind.ShouldBe(scalar.Kind);
        reparsed.Value.ShouldBe(scalar.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Node: nullable strings clear optional anchors and tags")]
    public void Node_NullProperties_RemovesOptionalAnchorAndTag()
    {
        // Arrange
        var node = YamlScalar.FromString("value");
        node.Anchor = "shared";
        node.Tag = "!local";
        string? absent = null;

        // Act
        node.Anchor = absent;
        node.Tag = absent;

        // Assert
        node.Anchor.ShouldBeNull();
        node.Tag.ShouldBeNull();
        YamlText.Write(new YamlDocument(node)).ShouldBe("value\n");
    }
}
