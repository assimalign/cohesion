using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlParserTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: block mapping with nested collections")]
    public void Parse_BlockMapping_Nested()
    {
        const string text = """
            name: Cohesion
            servers:
              - url: https://api.example.com
                env: production
              - url: https://staging.example.com
            limits:
              rate: 10
              burst: 20
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["name"]).Value.ShouldBe("Cohesion");

        var servers = (YamlSequence)root["servers"];
        servers.Count.ShouldBe(2);
        ((YamlScalar)((YamlMapping)servers[0])["env"]).Value.ShouldBe("production");
        ((YamlScalar)((YamlMapping)servers[1])["url"]).Value.ShouldBe("https://staging.example.com");

        var limits = (YamlMapping)root["limits"];
        ((YamlScalar)limits["rate"]).GetInteger().ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: sequence at the same indent as its key")]
    public void Parse_SequenceAtKeyIndent_IsValue()
    {
        const string text = """
            items:
            - one
            - two
            next: 3
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        var items = (YamlSequence)root["items"];
        items.Count.ShouldBe(2);
        ((YamlScalar)items[1]).Value.ShouldBe("two");
        ((YamlScalar)root["next"]).GetInteger().ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: flow collections and nesting")]
    public void Parse_FlowCollections_Nested()
    {
        var root = (YamlMapping)YamlText.ParseDocument("""{list: [1, two, {x: y}], empty: [], pair: {a: 1}}""").Root!;

        var list = (YamlSequence)root["list"];
        list.Style.ShouldBe(YamlCollectionStyle.Flow);
        list.Count.ShouldBe(3);
        ((YamlScalar)list[0]).GetInteger().ShouldBe(1);
        ((YamlScalar)((YamlMapping)list[2])["x"]).Value.ShouldBe("y");
        ((YamlSequence)root["empty"]).Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: single-pair mapping inside a flow sequence")]
    public void Parse_FlowPair_InSequence()
    {
        var root = (YamlSequence)YamlText.ParseDocument("[a: 1, plain]").Root!;

        root.Count.ShouldBe(2);
        var pair = (YamlMapping)root[0];
        ((YamlScalar)pair["a"]).GetInteger().ShouldBe(1);
        ((YamlScalar)root[1]).Value.ShouldBe("plain");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: comments are ignored everywhere")]
    public void Parse_Comments_Ignored()
    {
        const string text = """
            # leading comment
            key: value # trailing comment
            # between entries
            other: 2
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["key"]).Value.ShouldBe("value");
        ((YamlScalar)root["other"]).GetInteger().ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: multi-line plain scalars fold")]
    public void Parse_MultilinePlain_Folds()
    {
        const string text = """
            description: first part
              second part

              after blank
            next: 1
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["description"]).Value.ShouldBe("first part second part\nafter blank");
        ((YamlScalar)root["next"]).GetInteger().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: quoted scalar escapes and folding")]
    public void Parse_QuotedScalars_EscapesAndFolding()
    {
        var root = (YamlMapping)YamlText.ParseDocument("""
            dq: "line\nbreak\ttab A"
            sq: 'it''s folded
              here'
            """).Root!;

        ((YamlScalar)root["dq"]).Value.ShouldBe("line\nbreak\ttab A");
        ((YamlScalar)root["sq"]).Value.ShouldBe("it's folded here");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: anchors and aliases share the node instance")]
    public void Parse_Aliases_ShareInstance()
    {
        const string text = """
            base: &shared
              a: 1
            copy: *shared
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ReferenceEquals(root["base"], root["copy"]).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: undefined alias is a diagnostic error")]
    public void Parse_UndefinedAlias_Throws()
    {
        var exception = Should.Throw<YamlException>(() => YamlText.ParseDocument("a: *missing"));

        exception.Message.ShouldContain("missing");
        exception.Line.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: multi-document streams with markers")]
    public void Parse_MultiDocument_Streams()
    {
        const string text = """
            ---
            first: 1
            ...
            ---
            second: 2
            """;

        var stream = YamlText.Parse(text);

        stream.Count.ShouldBe(2);
        ((YamlScalar)((YamlMapping)stream[0].Root!)["first"]).GetInteger().ShouldBe(1);
        ((YamlScalar)((YamlMapping)stream[1].Root!)["second"]).GetInteger().ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: %TAG directives resolve named handles")]
    public void Parse_TagDirective_ResolvesHandle()
    {
        const string text = """
            %TAG !e! tag:example.com,2026:
            ---
            value: !e!widget data
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        root["value"].Tag.ShouldBe("tag:example.com,2026:widget");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: directives without a document marker are rejected")]
    public void Parse_DirectiveWithoutMarker_Throws()
    {
        Should.Throw<YamlException>(() => YamlText.Parse("%YAML 1.2\nkey: value"));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: explicit keys support complex keys")]
    public void Parse_ExplicitKey_Supported()
    {
        const string text = """
            ? [a, b]
            : both
            simple: 1
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        root.Count.ShouldBe(2);
        var complexKey = (YamlSequence)root.Entries[0].Key;
        complexKey.Count.ShouldBe(2);
        ((YamlScalar)root.Entries[0].Value).Value.ShouldBe("both");
        ((YamlScalar)root["simple"]).GetInteger().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: event pipeline reports positions")]
    public void ParseEvents_ReportsPositions()
    {
        var events = YamlText.ParseEvents("key: value");

        var scalarEvents = events.Where(e => e.Kind == YamlEventKind.Scalar).ToList();
        scalarEvents.Count.ShouldBe(2);
        scalarEvents[0].Value.ShouldBe("key");
        scalarEvents[0].Line.ShouldBe(1);
        scalarEvents[0].Column.ShouldBe(1);
        scalarEvents[1].Value.ShouldBe("value");
        scalarEvents[1].Column.ShouldBe(6);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: malformed input produces diagnostics")]
    [InlineData("key: \"unterminated")]
    [InlineData("[1, 2")]
    [InlineData("{a: 1")]
    [InlineData("key: \"bad \\q escape\"")]
    [InlineData("%YAML 2.0\n---\na: 1")]
    public void Parse_MalformedInput_Throws(string text)
    {
        Should.Throw<YamlException>(() => YamlText.Parse(text));
    }
}
