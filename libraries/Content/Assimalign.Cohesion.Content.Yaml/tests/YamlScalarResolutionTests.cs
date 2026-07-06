using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlScalarResolutionTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Schema: plain scalars resolve by the core schema")]
    [InlineData("null", YamlScalarKind.Null)]
    [InlineData("~", YamlScalarKind.Null)]
    [InlineData("", YamlScalarKind.Null)]
    [InlineData("true", YamlScalarKind.Boolean)]
    [InlineData("FALSE", YamlScalarKind.Boolean)]
    [InlineData("42", YamlScalarKind.Integer)]
    [InlineData("-7", YamlScalarKind.Integer)]
    [InlineData("0x1A", YamlScalarKind.Integer)]
    [InlineData("0o17", YamlScalarKind.Integer)]
    [InlineData("3.14", YamlScalarKind.Float)]
    [InlineData("-2e3", YamlScalarKind.Float)]
    [InlineData(".inf", YamlScalarKind.Float)]
    [InlineData(".nan", YamlScalarKind.Float)]
    [InlineData("yes", YamlScalarKind.String)]
    [InlineData("0x", YamlScalarKind.String)]
    [InlineData("1.2.3", YamlScalarKind.String)]
    [InlineData("text", YamlScalarKind.String)]
    public void Resolve_PlainScalar_UsesCoreSchema(string text, YamlScalarKind expected)
    {
        new YamlScalar(text).Kind.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Schema: typed accessors parse core-schema forms")]
    public void TypedAccessors_ParseValues()
    {
        new YamlScalar("0x1A").GetInteger().ShouldBe(26);
        new YamlScalar("0o17").GetInteger().ShouldBe(15);
        new YamlScalar("-42").GetInteger().ShouldBe(-42);
        new YamlScalar("3.5").GetDouble().ShouldBe(3.5);
        new YamlScalar(".inf").GetDouble().ShouldBe(double.PositiveInfinity);
        new YamlScalar("-.inf").GetDouble().ShouldBe(double.NegativeInfinity);
        double.IsNaN(new YamlScalar(".nan").GetDouble()).ShouldBeTrue();
        new YamlScalar("True").GetBoolean().ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Schema: quoted scalars are always strings")]
    public void Parse_QuotedScalars_AreStrings()
    {
        var document = YamlText.ParseDocument("""{a: "42", b: 'true'}""");
        var mapping = (YamlMapping)document.Root!;

        ((YamlScalar)mapping["a"]).Kind.ShouldBe(YamlScalarKind.String);
        ((YamlScalar)mapping["b"]).Kind.ShouldBe(YamlScalarKind.String);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Schema: FromString forces the string kind")]
    public void FromString_LooksLikeNumber_StaysString()
    {
        var scalar = YamlScalar.FromString("42");
        scalar.Kind.ShouldBe(YamlScalarKind.String);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Schema: explicit core tags override resolution")]
    public void Parse_ExplicitTags_OverrideResolution()
    {
        var document = YamlText.ParseDocument("a: !!str 42\nb: !!int '7'");
        var mapping = (YamlMapping)document.Root!;

        ((YamlScalar)mapping["a"]).Kind.ShouldBe(YamlScalarKind.String);
        ((YamlScalar)mapping["b"]).Kind.ShouldBe(YamlScalarKind.Integer);
    }
}
