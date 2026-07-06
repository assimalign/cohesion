using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlBlockScalarTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: literal scalar preserves line breaks")]
    public void Parse_Literal_PreservesBreaks()
    {
        const string text = """
            script: |
              line one
              line two
            next: 1
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["script"]).Value.ShouldBe("line one\nline two\n");
        ((YamlScalar)root["script"]).Style.ShouldBe(YamlScalarStyle.Literal);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: folded scalar folds line breaks")]
    public void Parse_Folded_FoldsBreaks()
    {
        const string text = """
            summary: >
              folded
              text

              after blank
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["summary"]).Value.ShouldBe("folded text\nafter blank\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: strip chomping removes the trailing break")]
    public void Parse_StripChomping_RemovesBreak()
    {
        var root = (YamlMapping)YamlText.ParseDocument("v: |-\n  text\n").Root!;

        ((YamlScalar)root["v"]).Value.ShouldBe("text");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: keep chomping preserves trailing breaks")]
    public void Parse_KeepChomping_PreservesBreaks()
    {
        var root = (YamlMapping)YamlText.ParseDocument("v: |+\n  text\n\nnext: 1").Root!;

        ((YamlScalar)root["v"]).Value.ShouldBe("text\n\n");
        ((YamlScalar)root["next"]).GetInteger().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: more-indented lines keep their breaks when folding")]
    public void Parse_Folded_MoreIndentedKeepsBreaks()
    {
        const string text = """
            v: >
              normal
                indented
              back
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["v"]).Value.ShouldBe("normal\n  indented\nback\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Block: literal content ends at a shallower indent")]
    public void Parse_Literal_EndsAtDedent()
    {
        const string text = """
            v: |
              content
            next: 2
            """;

        var root = (YamlMapping)YamlText.ParseDocument(text).Root!;

        ((YamlScalar)root["v"]).Value.ShouldBe("content\n");
        ((YamlScalar)root["next"]).GetInteger().ShouldBe(2);
    }
}
