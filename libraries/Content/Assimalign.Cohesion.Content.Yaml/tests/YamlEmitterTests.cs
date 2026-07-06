using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlEmitterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: block mapping with nested structures")]
    public void Write_BlockMapping_Nested()
    {
        var root = new YamlMapping
        {
            { "name", YamlScalar.FromString("Cohesion") },
            { "count", new YamlScalar(3L) }
        };
        var servers = new YamlSequence();
        servers.Add(new YamlMapping { { "url", YamlScalar.FromString("https://api.example.com") } });
        root.Add("servers", servers);

        var text = YamlText.Write(new YamlDocument(root));

        text.ShouldBe("""
            name: Cohesion
            count: 3
            servers:
              - url: https://api.example.com

            """.Replace("\r\n", "\n"));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: strings that look like other kinds are quoted")]
    public void Write_AmbiguousStrings_AreQuoted()
    {
        var root = new YamlMapping
        {
            { "a", YamlScalar.FromString("true") },
            { "b", YamlScalar.FromString("42") },
            { "c", YamlScalar.FromString("null") },
            { "d", YamlScalar.FromString("plain text") }
        };

        var text = YamlText.Write(new YamlDocument(root));

        text.ShouldContain("a: \"true\"", Case.Sensitive);
        text.ShouldContain("b: \"42\"", Case.Sensitive);
        text.ShouldContain("c: \"null\"", Case.Sensitive);
        text.ShouldContain("d: plain text", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: multi-line strings use literal blocks")]
    public void Write_MultilineString_UsesLiteralBlock()
    {
        var root = new YamlMapping { { "script", YamlScalar.FromString("one\ntwo\n") } };

        var text = YamlText.Write(new YamlDocument(root));

        text.ShouldBe("script: |\n  one\n  two\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: flow styles are preserved")]
    public void Write_FlowStyles_Preserved()
    {
        var sequence = new YamlSequence { Style = YamlCollectionStyle.Flow };
        sequence.Add(new YamlScalar(1L));
        sequence.Add(new YamlScalar(2L));
        var root = new YamlMapping { { "flow", sequence }, { "empty", new YamlMapping() } };

        var text = YamlText.Write(new YamlDocument(root));

        text.ShouldContain("flow: [1, 2]", Case.Sensitive);
        text.ShouldContain("empty: {}", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: shared nodes get anchors and aliases")]
    public void Write_SharedNodes_UseAnchors()
    {
        var shared = new YamlMapping { { "a", new YamlScalar(1L) } };
        var root = new YamlMapping { { "base", shared }, { "copy", shared } };

        var text = YamlText.Write(new YamlDocument(root));
        var reparsed = (YamlMapping)YamlText.ParseDocument(text).Root!;

        text.ShouldContain("&", Case.Sensitive);
        text.ShouldContain("*", Case.Sensitive);
        ReferenceEquals(reparsed["base"], reparsed["copy"]).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Emit: multi-document streams use markers")]
    public void Write_MultiDocument_UsesMarkers()
    {
        var stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(new YamlMapping { { "first", new YamlScalar(1L) } }));
        stream.Documents.Add(new YamlDocument(new YamlMapping { { "second", new YamlScalar(2L) } }));

        var text = YamlText.Write(stream);
        var reparsed = YamlText.Parse(text);

        reparsed.Count.ShouldBe(2);
        ((YamlScalar)((YamlMapping)reparsed[1].Root!)["second"]).GetInteger().ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Emit: write-parse-write round-trips are stable")]
    [InlineData("name: Cohesion\nitems:\n  - a\n  - b\n")]
    [InlineData("map:\n  x: 1\n  y: [1, 2, {z: true}]\n")]
    [InlineData("text: |\n  first\n  second\n")]
    [InlineData("quoted: \"a: b\"\nnullish: null\n")]
    [InlineData("nested:\n  - - deep\n  - other\n")]
    public void Write_RoundTrip_IsStable(string text)
    {
        var first = YamlText.Write(YamlText.Parse(text));
        var second = YamlText.Write(YamlText.Parse(first));

        second.ShouldBe(first);
    }
}
